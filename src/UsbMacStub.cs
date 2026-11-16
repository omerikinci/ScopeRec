// macOS/Linux build placeholder.
// The original Usb implementation uses Windows WinUSB APIs and is still used by build.bat.
class Usb
{
    public static ILink Open(uint timeoutMs)
    {
        return null;
    }
}
