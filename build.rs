fn main() {
    #[cfg(windows)]
    {
        let mut resource = winres::WindowsResource::new();
        resource.set_icon("assets/app.ico");
        if std::env::var("PROFILE").as_deref() == Ok("release") {
            resource.set_manifest_file("app.manifest");
        }
        resource
            .compile()
            .expect("failed to embed Windows resources");
    }
}
