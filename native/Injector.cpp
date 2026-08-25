#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <string>
#include <vector>
#include <set>
#include <iostream>
#include <filesystem>
#include <algorithm>
#include <cwctype>

static std::wstring Lower(std::wstring s) {
    std::transform(s.begin(), s.end(), s.begin(), ::towlower);
    return s;
}

static std::vector<DWORD> FindProcesses(const std::wstring& exeName) {
    std::vector<DWORD> out;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return out;
    PROCESSENTRY32W e{};
    e.dwSize = sizeof(e);
    if (Process32FirstW(snap, &e)) {
        const auto wanted = Lower(exeName);
        do {
            if (Lower(e.szExeFile) == wanted) out.push_back(e.th32ProcessID);
        } while (Process32NextW(snap, &e));
    }
    CloseHandle(snap);
    return out;
}

static bool Inject(DWORD pid, const std::wstring& dllPath) {
    HANDLE proc = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
    if (!proc) {
        std::wcerr << L"[inject] OpenProcess failed PID=" << pid << L" err=" << GetLastError() << L"\n";
        return false;
    }

    const SIZE_T bytes = (dllPath.size() + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(proc, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) {
        std::wcerr << L"[inject] VirtualAllocEx failed PID=" << pid << L" err=" << GetLastError() << L"\n";
        CloseHandle(proc);
        return false;
    }

    bool ok = WriteProcessMemory(proc, remote, dllPath.c_str(), bytes, nullptr) != FALSE;
    auto loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(GetModuleHandleW(L"Kernel32.dll"), "LoadLibraryW"));
    HANDLE thread = ok && loadLibrary ? CreateRemoteThread(proc, nullptr, 0, loadLibrary, remote, 0, nullptr) : nullptr;
    if (thread) {
        DWORD wait = WaitForSingleObject(thread, 10000);
        DWORD result = 0;
        if (wait == WAIT_OBJECT_0 && GetExitCodeThread(thread, &result)) ok = result != 0;
        else ok = false;
        CloseHandle(thread);
    } else {
        ok = false;
    }

    VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
    CloseHandle(proc);
    std::wcout << L"[inject] PID=" << pid << (ok ? L" OK\n" : L" FAILED\n");
    return ok;
}

static bool LaunchSuspendedAndInject(const std::wstring& exePath, const std::wstring& dllPath, bool waitForExit) {
    const std::filesystem::path exeFs = std::filesystem::absolute(exePath);
    if (!std::filesystem::exists(exeFs)) {
        std::wcerr << L"[early] executable not found: " << exeFs.wstring() << L"\n";
        return false;
    }

    std::wstring commandLine = L"\"" + exeFs.wstring() + L"\"";
    std::vector<wchar_t> mutableCommand(commandLine.begin(), commandLine.end());
    mutableCommand.push_back(L'\0');

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    const auto cwd = exeFs.parent_path().wstring();

    std::wcout << L"[early] CREATE_SUSPENDED exe=" << exeFs.wstring() << L"\n";
    BOOL created = CreateProcessW(
        exeFs.c_str(),
        mutableCommand.data(),
        nullptr,
        nullptr,
        FALSE,
        CREATE_SUSPENDED,
        nullptr,
        cwd.empty() ? nullptr : cwd.c_str(),
        &si,
        &pi);

    if (!created) {
        std::wcerr << L"[early] CreateProcessW failed err=" << GetLastError() << L"\n";
        return false;
    }

    bool injected = Inject(pi.dwProcessId, dllPath);
    if (!injected) {
        std::wcerr << L"[early] injection failed before resume; terminating suspended target\n";
        TerminateProcess(pi.hProcess, 90);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return false;
    }

    // LoadLibrary returns before LaserOSHook's worker thread is guaranteed to finish
    // its first IAT patch pass. Keep the primary target thread suspended long enough
    // for that first pass, so startup discovery cannot race ahead of the hook.
    Sleep(1200);
    std::wcout << L"[early] hook settle barrier complete\n";

    DWORD previousSuspend = ResumeThread(pi.hThread);
    if (previousSuspend == static_cast<DWORD>(-1)) {
        std::wcerr << L"[early] ResumeThread failed err=" << GetLastError() << L"\n";
        TerminateProcess(pi.hProcess, 91);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return false;
    }

    std::wcout << L"[early] resumed PID=" << pi.dwProcessId << L" after hook injection\n";
    bool ok = true;
    if (waitForExit) {
        DWORD wait = WaitForSingleObject(pi.hProcess, 15000);
        if (wait != WAIT_OBJECT_0) {
            std::wcerr << L"[early] launched target did not exit within test timeout\n";
            ok = false;
        } else {
            DWORD exitCode = 0;
            if (!GetExitCodeProcess(pi.hProcess, &exitCode) || exitCode != 0) {
                std::wcerr << L"[early] launched target exit=" << exitCode << L"\n";
                ok = false;
            }
        }
    }

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return ok;
}

int wmain(int argc, wchar_t** argv) {
    std::wstring processName = L"LaserOS.exe";
    std::wstring dllPath = L"LaserOSHook.dll";
    std::wstring launchPath;
    bool once = false;

    for (int i = 1; i < argc; ++i) {
        std::wstring a = argv[i];
        if (a == L"--process" && i + 1 < argc) processName = argv[++i];
        else if (a == L"--dll" && i + 1 < argc) dllPath = argv[++i];
        else if (a == L"--launch" && i + 1 < argc) launchPath = argv[++i];
        else if (a == L"--once") once = true;
        else if (a == L"--help") {
            std::wcout << L"Cube7 LaserOS injector (local process only)\n"
                          L"  --process LaserOS.exe\n"
                          L"  --dll LaserOSHook.dll\n"
                          L"  --launch <path-to-exe>   Start target suspended, inject, then resume\n"
                          L"  --once                   Single pass; with --launch waits for target exit (CI probe)\n";
            return 0;
        }
    }

    dllPath = std::filesystem::absolute(dllPath).wstring();
    if (!std::filesystem::exists(dllPath)) {
        std::wcerr << L"DLL not found: " << dllPath << L"\n";
        return 2;
    }

    if (!launchPath.empty()) {
        return LaunchSuspendedAndInject(launchPath, dllPath, once) ? 0 : 3;
    }

    std::wcout << L"[watch] process=" << processName << L" dll=" << dllPath << L"\n";
    std::set<DWORD> injected;
    do {
        auto pids = FindProcesses(processName);
        std::set<DWORD> alive(pids.begin(), pids.end());
        for (auto it = injected.begin(); it != injected.end();) {
            if (!alive.count(*it)) it = injected.erase(it); else ++it;
        }
        for (DWORD pid : pids) {
            if (!injected.count(pid) && Inject(pid, dllPath)) injected.insert(pid);
        }
        if (once) break;
        Sleep(1000);
    } while (true);
    return 0;
}
