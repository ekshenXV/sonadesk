#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>

enum { PathCapacity = 32768, CommandCapacity = 32767 };

static wchar_t launcher[PathCapacity];
static wchar_t directory[PathCapacity];
static wchar_t executable[PathCapacity];
static wchar_t command[CommandCapacity];

static int launch_error(BOOL missing_files)
{
    BOOL russian = PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_RUSSIAN;
    const wchar_t *message;
    if (missing_files) {
        message = russian ? L"Распакуй весь ZIP-архив и запусти MicEffects.exe."
                          : L"Extract the entire ZIP archive, then run MicEffects.exe.";
    } else {
        message = russian ? L"Не удалось запустить Mic Effects."
                          : L"Could not start Mic Effects.";
    }
    MessageBoxW(NULL, message, L"Mic Effects", MB_OK | MB_ICONERROR);
    return 1;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show)
{
    const wchar_t suffix[] = L"app\\MicEffects.exe";
    DWORD length = GetModuleFileNameW(NULL, launcher, ARRAYSIZE(launcher));
    if (length == 0 || length >= ARRAYSIZE(launcher)) return launch_error(FALSE);

    const wchar_t *separator = wcsrchr(launcher, L'\\');
    if (separator == NULL) return launch_error(FALSE);
    size_t parent_length = separator - launcher + 1;
    if (parent_length + ARRAYSIZE(suffix) > ARRAYSIZE(executable)) return launch_error(FALSE);

    wmemcpy(executable, launcher, parent_length);
    wcscpy(executable + parent_length, suffix);
    wmemcpy(directory, launcher, parent_length);
    wcscpy(directory + parent_length, L"app");

    size_t executable_length = wcslen(executable);
    if (executable_length + wcslen(arguments) + 4 > ARRAYSIZE(command)) return launch_error(FALSE);
    command[0] = L'"';
    wmemcpy(command + 1, executable, executable_length);
    command[executable_length + 1] = L'"';
    command[executable_length + 2] = L' ';
    wcscpy(command + executable_length + 3, arguments);
    if (!SetEnvironmentVariableW(L"PORTABLE_EXECUTABLE_FILE", launcher)) return launch_error(FALSE);

    STARTUPINFOW startup = {0};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION child;
    if (!CreateProcessW(executable, command, NULL, NULL, FALSE, 0, NULL, directory, &startup, &child)) {
        DWORD error = GetLastError();
        return launch_error(error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND || error == ERROR_MOD_NOT_FOUND);
    }
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);
    return 0;
}
