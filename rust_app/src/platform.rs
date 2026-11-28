use std::process::{Child, Command};
#[derive(Default)]
pub struct SleepGuard {
    child: Option<Child>,
    active: bool,
}
impl SleepGuard {
    pub fn set(&mut self, active: bool) {
        if active == self.active {
            return;
        }
        self.active = active;
        #[cfg(target_os = "macos")]
        {
            if active {
                self.child = Command::new("/usr/bin/caffeinate")
                    .args(["-i", "-w", &std::process::id().to_string()])
                    .spawn()
                    .ok();
            } else if let Some(mut p) = self.child.take() {
                let _ = p.kill();
                let _ = p.wait();
            }
        }
        #[cfg(windows)]
        unsafe {
            use windows_sys::Win32::System::Power::{
                ES_CONTINUOUS, ES_SYSTEM_REQUIRED, SetThreadExecutionState,
            };
            SetThreadExecutionState(if active {
                ES_CONTINUOUS | ES_SYSTEM_REQUIRED
            } else {
                ES_CONTINUOUS
            });
        }
    }
}
impl Drop for SleepGuard {
    fn drop(&mut self) {
        self.set(false);
    }
}
pub fn beep() {
    #[cfg(target_os = "macos")]
    {
        let _ = Command::new("/usr/bin/osascript")
            .args(["-e", "beep"])
            .spawn();
    }
    #[cfg(windows)]
    unsafe {
        windows_sys::Win32::System::Diagnostics::Debug::MessageBeep(0);
    }
}
pub fn reveal(path: &std::path::Path) {
    #[cfg(target_os = "macos")]
    {
        let _ = Command::new("open").arg(path).spawn();
    }
    #[cfg(windows)]
    {
        let _ = Command::new("explorer").arg(path).spawn();
    }
    #[cfg(target_os = "linux")]
    {
        let _ = Command::new("xdg-open").arg(path).spawn();
    }
}
