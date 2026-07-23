#![windows_subsystem = "windows"]

mod service;

use service::{AccelService, OperationResult};
use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::sync::mpsc::{self, Receiver, TryRecvError};
use std::thread;
use windows::core::PCWSTR;
use windows::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::Graphics::Gdi::{
    CreateFontW, DEFAULT_CHARSET, DEFAULT_PITCH, FF_DONTCARE, FW_NORMAL, HBRUSH, HFONT,
    OUT_DEFAULT_PRECIS,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::HiDpi::{
    GetDpiForSystem, SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
};
use windows::Win32::UI::Input::KeyboardAndMouse::EnableWindow;
use windows::Win32::UI::WindowsAndMessaging::*;

const ACCELERATE: usize = 1001;
const RESTORE: usize = 1002;
const OPEN_STATE: usize = 1003;
const TIMER: usize = 1;
const EDIT_SET_SELECTION: u32 = 0x00B1;
const EDIT_REPLACE_SELECTION: u32 = 0x00C2;

enum WorkerMessage {
    Log(String),
    Done(OperationResult),
}
struct AppState {
    service: AccelService,
    status: HWND,
    selected: HWND,
    report: HWND,
    group: HWND,
    log: HWND,
    buttons: [HWND; 3],
    receiver: Option<Receiver<WorkerMessage>>,
    _font: HFONT,
    _log_font: HFONT,
}

fn wide(value: impl AsRef<OsStr>) -> Vec<u16> {
    value
        .as_ref()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}
#[allow(clippy::manual_dangling_ptr)]
fn integer_resource(id: u16) -> PCWSTR {
    PCWSTR(id as usize as *const u16)
}
fn set_text(hwnd: HWND, value: &str) {
    let text = wide(value);
    unsafe {
        SetWindowTextW(hwnd, PCWSTR(text.as_ptr())).ok();
    }
}
fn label(parent: HWND, text: &str, x: i32, y: i32, w: i32, h: i32, font: HFONT) -> HWND {
    let class = wide("STATIC");
    let text = wide(text);
    unsafe {
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            PCWSTR(class.as_ptr()),
            PCWSTR(text.as_ptr()),
            WS_CHILD | WS_VISIBLE,
            x,
            y,
            w,
            h,
            parent,
            HMENU::default(),
            HINSTANCE::default(),
            None,
        )
        .unwrap();
        SendMessageW(hwnd, WM_SETFONT, WPARAM(font.0 as usize), LPARAM(1));
        hwnd
    }
}
fn button(parent: HWND, text: &str, id: usize, x: i32, y: i32, w: i32, font: HFONT) -> HWND {
    let class = wide("BUTTON");
    let text = wide(text);
    unsafe {
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            PCWSTR(class.as_ptr()),
            PCWSTR(text.as_ptr()),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(BS_PUSHBUTTON as u32),
            x,
            y,
            w,
            40,
            parent,
            HMENU(id as *mut std::ffi::c_void),
            HINSTANCE::default(),
            None,
        )
        .unwrap();
        SendMessageW(hwnd, WM_SETFONT, WPARAM(font.0 as usize), LPARAM(1));
        hwnd
    }
}
fn append_log(hwnd: HWND, message: &str) {
    let timestamp = chrono::Local::now().format("%H:%M:%S").to_string();
    let normalized = message.replace("\r\n", "\n").replace('\r', "\n");
    let mut formatted = String::new();
    for line in normalized.lines() {
        formatted.push_str(&format!("[{timestamp}] {line}\r\n"));
    }
    if formatted.is_empty() {
        formatted = format!("[{timestamp}]\r\n");
    }
    let text = wide(formatted);
    unsafe {
        let len = GetWindowTextLengthW(hwnd);
        SendMessageW(
            hwnd,
            EDIT_SET_SELECTION,
            WPARAM(len as usize),
            LPARAM(len as isize),
        );
        SendMessageW(
            hwnd,
            EDIT_REPLACE_SELECTION,
            WPARAM(0),
            LPARAM(text.as_ptr() as isize),
        );
    }
}
fn show_message(parent: HWND, result: &OperationResult) {
    let text = wide(&result.message);
    let title = wide(if result.success { "完成" } else { "失败" });
    unsafe {
        MessageBoxW(
            parent,
            PCWSTR(text.as_ptr()),
            PCWSTR(title.as_ptr()),
            if result.success {
                MB_OK | MB_ICONINFORMATION
            } else {
                MB_OK | MB_ICONERROR
            },
        );
    }
}
fn refresh_report(state: &AppState) {
    set_text(
        state.report,
        &format!("测速报告：{}", state.service.last_result_path().display()),
    );
    let selected = state.service.last_report().map_or_else(
        || "上次优选 IP：暂无".into(),
        |r| {
            format!(
                "上次优选 IP：{}  |  {}",
                r.selected_ip,
                r.generated_at.format("%Y-%m-%d %H:%M:%S")
            )
        },
    );
    set_text(state.selected, &selected);
}
fn set_busy(state: &AppState, busy: bool, status: &str) {
    set_controls_enabled(state, !busy);
    set_text(state.status, status);
}
fn set_controls_enabled(state: &AppState, enabled: bool) {
    for button in state.buttons {
        unsafe {
            let _ = EnableWindow(button, enabled);
        }
    }
    unsafe {
        SetCursor(LoadCursorW(None, if enabled { IDC_ARROW } else { IDC_WAIT }).unwrap());
    }
}
fn start_operation(hwnd: HWND, state: &mut AppState, accelerate: bool) {
    if state.receiver.is_some() {
        return;
    }
    set_busy(
        state,
        true,
        if accelerate {
            "状态：正在测速并写入 hosts..."
        } else {
            "状态：正在恢复 hosts..."
        },
    );
    append_log(state.log, &"-".repeat(56));
    let (tx, rx) = mpsc::channel();
    let service = state.service.clone();
    state.receiver = Some(rx);
    thread::spawn(move || {
        let log_tx = tx.clone();
        let send = move |message: String| {
            let _ = log_tx.send(WorkerMessage::Log(message));
        };
        let result = if accelerate {
            service.accelerate_with_logger(send)
        } else {
            service.restore_with_logger(send)
        };
        let _ = tx.send(WorkerMessage::Done(result));
    });
    unsafe {
        SetTimer(hwnd, TIMER, 100, None);
    }
}

unsafe extern "system" fn wnd_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    let state_ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut AppState;
    if message == WM_NCCREATE {
        let create = &*(lparam.0 as *const CREATESTRUCTW);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, create.lpCreateParams as isize);
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }
    if state_ptr.is_null() {
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }
    let state = &mut *state_ptr;
    match message {
        WM_COMMAND => {
            if wparam.0 & 0xffff == ACCELERATE {
                start_operation(hwnd, state, true);
            } else if wparam.0 & 0xffff == RESTORE {
                start_operation(hwnd, state, false);
            } else if wparam.0 & 0xffff == OPEN_STATE {
                let _ = std::process::Command::new("explorer.exe")
                    .arg(state.service.state_directory())
                    .spawn();
            }
            LRESULT(0)
        }
        WM_TIMER => {
            if let Some(rx) = &state.receiver {
                loop {
                    match rx.try_recv() {
                        Ok(WorkerMessage::Log(message)) => append_log(state.log, &message),
                        Ok(WorkerMessage::Done(result)) => {
                            set_text(
                                state.status,
                                if result.success {
                                    "状态：完成"
                                } else {
                                    "状态：失败"
                                },
                            );
                            append_log(state.log, &result.message);
                            refresh_report(state);
                            show_message(hwnd, &result);
                            set_controls_enabled(state, true);
                            state.receiver = None;
                            KillTimer(hwnd, TIMER).ok();
                            break;
                        }
                        Err(TryRecvError::Empty) => break,
                        Err(TryRecvError::Disconnected) => {
                            state.receiver = None;
                            set_busy(state, false, "状态：失败");
                            KillTimer(hwnd, TIMER).ok();
                            break;
                        }
                    }
                }
            }
            LRESULT(0)
        }
        WM_SIZE => {
            let mut rect = windows::Win32::Foundation::RECT::default();
            if GetClientRect(hwnd, &mut rect).is_ok() {
                let width = (rect.right - rect.left).max(300);
                let height = (rect.bottom - rect.top).max(260);
                let _ = SetWindowPos(
                    state.group,
                    HWND::default(),
                    10,
                    172,
                    width - 20,
                    height - 182,
                    SWP_NOZORDER,
                );
                let _ = SetWindowPos(
                    state.log,
                    HWND::default(),
                    22,
                    196,
                    width - 34,
                    height - 218,
                    SWP_NOZORDER,
                );
            }
            LRESULT(0)
        }
        WM_GETMINMAXINFO => {
            let limits = &mut *(lparam.0 as *mut MINMAXINFO);
            limits.ptMinTrackSize.x = 760;
            limits.ptMinTrackSize.y = 560;
            LRESULT(0)
        }
        WM_DESTROY => {
            let _ = Box::from_raw(state_ptr);
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, message, wparam, lparam),
    }
}

fn create_window(instance: HINSTANCE) -> windows::core::Result<HWND> {
    let class = wide("OsuNetworkAccelWindow");
    let title = wide("osu! Network Accel");
    let dpi = unsafe { GetDpiForSystem() } as i32;
    let font = unsafe {
        CreateFontW(
            -(9 * dpi / 72),
            0,
            0,
            0,
            FW_NORMAL.0 as i32,
            0,
            0,
            0,
            DEFAULT_CHARSET.0 as u32,
            OUT_DEFAULT_PRECIS.0 as u32,
            0,
            0,
            DEFAULT_PITCH.0 as u32 | FF_DONTCARE.0 as u32,
            PCWSTR(wide("Segoe UI").as_ptr()),
        )
    };
    let log_font = unsafe {
        CreateFontW(
            -(10 * dpi / 72),
            0,
            0,
            0,
            FW_NORMAL.0 as i32,
            0,
            0,
            0,
            DEFAULT_CHARSET.0 as u32,
            OUT_DEFAULT_PRECIS.0 as u32,
            0,
            0,
            DEFAULT_PITCH.0 as u32 | FF_DONTCARE.0 as u32,
            PCWSTR(wide("Consolas").as_ptr()),
        )
    };
    let service = AccelService::new();
    let state = Box::new(AppState {
        service,
        status: HWND::default(),
        selected: HWND::default(),
        report: HWND::default(),
        group: HWND::default(),
        log: HWND::default(),
        buttons: [HWND::default(); 3],
        receiver: None,
        _font: font,
        _log_font: log_font,
    });
    let state_ptr = Box::into_raw(state);
    unsafe {
        let wc = WNDCLASSW {
            lpfnWndProc: Some(wnd_proc),
            hInstance: instance,
            lpszClassName: PCWSTR(class.as_ptr()),
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap(),
            hIcon: LoadIconW(instance, integer_resource(1)).unwrap_or_default(),
            hbrBackground: HBRUSH(6usize as *mut std::ffi::c_void),
            ..Default::default()
        };
        RegisterClassW(&wc);
        let hwnd = CreateWindowExW(
            WS_EX_APPWINDOW,
            PCWSTR(class.as_ptr()),
            PCWSTR(title.as_ptr()),
            WS_OVERLAPPEDWINDOW,
            0,
            0,
            760,
            560,
            HWND::default(),
            HMENU::default(),
            instance,
            Some(state_ptr as *const _),
        )
        .unwrap();
        let state = &mut *state_ptr;
        state.status = label(hwnd, "状态：待命", 20, 44, 700, 22, font);
        label(
            hwnd,
            "通过修改 Windows hosts 为 osu! 选择当前更快的 Cloudflare IP。",
            20,
            18,
            700,
            22,
            font,
        );
        state.selected = label(hwnd, "上次优选 IP：暂无", 20, 70, 700, 22, font);
        state.report = label(
            hwnd,
            &format!("测速报告：{}", state.service.last_result_path().display()),
            20,
            96,
            700,
            22,
            font,
        );
        state.buttons = [
            button(hwnd, "测速并加速", ACCELERATE, 20, 125, 115, font),
            button(hwnd, "恢复原本网络", RESTORE, 143, 125, 130, font),
            button(hwnd, "打开报告目录", OPEN_STATE, 281, 125, 130, font),
        ];
        let group_class = wide("BUTTON");
        let group_text = wide("日志");
        state.group = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            PCWSTR(group_class.as_ptr()),
            PCWSTR(group_text.as_ptr()),
            WS_CHILD | WS_VISIBLE | WINDOW_STYLE(BS_GROUPBOX as u32),
            10,
            172,
            730,
            350,
            hwnd,
            HMENU::default(),
            instance,
            None,
        )
        .unwrap();
        SendMessageW(state.group, WM_SETFONT, WPARAM(font.0 as usize), LPARAM(1));
        let edit_class = wide("EDIT");
        state.log = CreateWindowExW(
            WS_EX_CLIENTEDGE,
            PCWSTR(edit_class.as_ptr()),
            PCWSTR(wide("").as_ptr()),
            WS_CHILD
                | WS_VISIBLE
                | WS_VSCROLL
                | WINDOW_STYLE((ES_MULTILINE | ES_AUTOVSCROLL | ES_READONLY) as u32),
            22,
            196,
            706,
            304,
            hwnd,
            HMENU::default(),
            instance,
            None,
        )
        .unwrap();
        SendMessageW(
            state.log,
            WM_SETFONT,
            WPARAM(log_font.0 as usize),
            LPARAM(1),
        );
        refresh_report(state);
        let screen_w = GetSystemMetrics(SM_CXSCREEN);
        let screen_h = GetSystemMetrics(SM_CYSCREEN);
        let _ = SetWindowPos(
            hwnd,
            HWND::default(),
            (screen_w - 760) / 2,
            (screen_h - 560) / 2,
            760,
            560,
            SWP_NOZORDER,
        );
        let _ = ShowWindow(hwnd, SW_SHOW);
        Ok(hwnd)
    }
}

fn main() -> windows::core::Result<()> {
    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        let instance = HINSTANCE(GetModuleHandleW(None)?.0);
        let _ = create_window(instance)?;
        let mut message = MSG::default();
        while GetMessageW(&mut message, None, 0, 0).into() {
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
    Ok(())
}
