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
    if (!remote) { CloseHandle(proc); return false; }

    bool ok = WriteProcessMemory(proc, remote, dllPath.c_str(), bytes, nullptr) != FALSE;
    auto loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(GetModuleHandleW(L"Kernel32.dll"), "LoadLibraryW"));
    HANDLE thread = ok ? CreateRemoteThread(proc, nullptr, 0, loadLibrary, remote, 0, nullptr) : nullptr;
    if (thread) {
        WaitForSingleObject(thread, 10000);
        DWORD result = 0;
        GetExitCodeThread(thread, &result);
        ok = result != 0;
        CloseHandle(thread);
    } else {
        ok = false;
    }

    VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
    CloseHandle(proc);
    std::wcout << L"[inject] PID=" << pid << (ok ? L" OK\n" : L" FAILED\n");
    return ok;
}

int wmain(int argc, wchar_t** argv) {
    std::wstring processName = L"LaserOS.exe";
    std::wstring dllPath = L"LaserOSHook.dll";
    bool once = false;

    for (int i = 1; i < argc; ++i) {
        std::wstring a = argv[i];
        if (a == L"--process" && i + 1 < argc) processName = argv[++i];
        else if (a == L"--dll" && i + 1 < argc) dllPath = argv[++i];
        else if (a == L"--once") once = true;
        else if (a == L"--help") {
            std::wcout << L"Cube7 LaserOS injector (local process only)\n"
                          L"  --process LaserOS.exe\n  --dll LaserOSHook.dll\n  --once\n";
            return 0;
        }
    }

    dllPath = std::filesystem::absolute(dllPath).wstring();
    if (!std::filesystem::exists(dllPath)) {
        std::wcerr << L"DLL not found: " << dllPath << L"\n";
        return 2;
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
