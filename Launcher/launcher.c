/*
 * DSPi Console launcher.
 *
 * The release folder keeps a single executable at its root; everything the app
 * actually needs lives in "app\". That split exists because this is an
 * unpackaged WinUI 3 app: the WindowsAppSDK native DLLs are loaded by the OS
 * from the executable's own directory, so the real DSPiConsole.exe cannot be
 * separated from them. Moving the whole payload into a subfolder and starting
 * it from here keeps that requirement intact.
 *
 * The launcher exits as soon as the app is started; it does not stay resident.
 * Built with MinGW: see build.ps1.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#define APP_SUBDIR   L"\\app"
#define APP_EXE_NAME L"\\DSPiConsole.exe"

static void fail(const wchar_t *msg)
{
    MessageBoxW(NULL, msg, L"DSPi Console", MB_ICONERROR | MB_OK);
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE hPrev, PWSTR lpCmdLine, int nShow)
{
    (void)hInst;
    (void)hPrev;

    /* Directory containing this launcher. */
    wchar_t dir[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, dir, MAX_PATH);
    if (n == 0 || n >= MAX_PATH)
    {
        fail(L"DSPi Console could not determine where it was started from.");
        return 1;
    }
    wchar_t *slash = wcsrchr(dir, L'\\');
    if (slash == NULL)
    {
        fail(L"DSPi Console could not determine where it was started from.");
        return 1;
    }
    *slash = L'\0';

    wchar_t appDir[MAX_PATH + 16];
    wchar_t appExe[MAX_PATH + 48];
    lstrcpynW(appDir, dir, MAX_PATH);
    lstrcatW(appDir, APP_SUBDIR);
    lstrcpyW(appExe, appDir);
    lstrcatW(appExe, APP_EXE_NAME);

    if (GetFileAttributesW(appExe) == INVALID_FILE_ATTRIBUTES)
    {
        fail(L"DSPi Console could not find its program files.\n\n"
             L"Expected to find app\\DSPiConsole.exe next to this launcher. "
             L"Extract the whole release folder and keep its structure intact, "
             L"then run DSPi Console again.");
        return 1;
    }

    /*
     * Command line for the child: the quoted target, then anything we were
     * given. CreateProcessW may write to this buffer, so it must be writable.
     * Arguments are dropped rather than truncated if they would not fit, since
     * a half-copied command line is worse than none.
     */
    static wchar_t cmdline[32768];
    const int exeLen = lstrlenW(appExe);
    const int argLen = (lpCmdLine != NULL) ? lstrlenW(lpCmdLine) : 0;

    cmdline[0] = L'"';
    lstrcpyW(cmdline + 1, appExe);
    lstrcatW(cmdline, L"\"");
    if (argLen > 0 && (exeLen + argLen + 4) < (int)(sizeof(cmdline) / sizeof(cmdline[0])))
    {
        lstrcatW(cmdline, L" ");
        lstrcatW(cmdline, lpCmdLine);
    }

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = (WORD)nShow;
    ZeroMemory(&pi, sizeof(pi));

    /* Working directory is the app folder so relative paths resolve as they
     * did when the executable sat in the release root. */
    if (!CreateProcessW(appExe, cmdline, NULL, NULL, FALSE, 0, NULL, appDir, &si, &pi))
    {
        fail(L"DSPi Console could not be started.\n\n"
             L"app\\DSPiConsole.exe is present but would not launch. It may be "
             L"blocked by security software, or the download may be incomplete.");
        return 1;
    }

    /* Let the app take the foreground, then get out of the way. */
    AllowSetForegroundWindow(pi.dwProcessId);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;
}
