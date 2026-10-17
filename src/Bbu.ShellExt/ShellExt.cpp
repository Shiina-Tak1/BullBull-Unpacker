#include <winternl.h>

#include "ShellExt.h"

#include <strsafe.h>

#include <string>
#include <vector>

namespace
{
    LONG g_objectCount = 0;
    HMODULE g_module = nullptr;

    // ── 程序本体在哪 ──

    std::wstring ModuleDirectory()
    {
        wchar_t path[MAX_PATH * 4]{};
        if (g_module == nullptr || GetModuleFileNameW(g_module, path, ARRAYSIZE(path)) == 0)
        {
            return {};
        }

        std::wstring full(path);
        const size_t slash = full.find_last_of(L'\\');
        return slash == std::wstring::npos ? std::wstring{} : full.substr(0, slash);
    }

    bool FileExists(const std::wstring& path)
    {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    // 界面那个菜单显示开关。读不到、类型不对，都按「显示」——默认态不该因为一次读失败就变。
    bool SwitchEnabled(const wchar_t* value)
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(HKEY_CURRENT_USER, BBU_TARGET_KEY, 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS)
        {
            return true;
        }

        DWORD data = 0;
        DWORD size = sizeof(data);
        DWORD type = 0;
        const LSTATUS status = RegQueryValueExW(
            key, value, nullptr, &type, reinterpret_cast<LPBYTE>(&data), &size);
        RegCloseKey(key);

        return status != ERROR_SUCCESS || type != REG_DWORD ? true : data != 0;
    }

    // 当前是不是 Windows 11 或更高。用 RtlGetVersion 而不是 GetVersionEx：后者按清单里的
    // supportedOS 不可信，本组件没声明那一段，拿到的会是 6.2。
    bool IsWindows11OrGreater()
    {
        using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
        static const RtlGetVersionFn query = reinterpret_cast<RtlGetVersionFn>(
            GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
        if (query == nullptr)
        {
            // 查不出来就当作不是 Win11：宁可多插一项（看得见），也不要少插一项（没入口）
            return false;
        }

        RTL_OSVERSIONINFOW info{};
        info.dwOSVersionInfoSize = sizeof(info);
        return query(&info) == 0 && info.dwBuildNumber >= 22000;
    }

    std::wstring ReadTargetFromFile()
    {
        wchar_t folder[MAX_PATH * 4]{};
        if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, folder)))
        {
            return {};
        }

        const std::wstring path = std::wstring(folder) + L"\\" + BBU_TARGET_RELATIVE;
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return {};
        }

        char raw[MAX_PATH * 4]{};
        DWORD read = 0;
        const BOOL ok = ReadFile(file, raw, sizeof(raw) - 1, &read, nullptr);
        CloseHandle(file);
        if (!ok || read == 0)
        {
            return {};
        }

        // 文件按 UTF-8 写，含中文路径
        const int wide = MultiByteToWideChar(CP_UTF8, 0, raw, static_cast<int>(read), nullptr, 0);
        std::wstring text(static_cast<size_t>(wide), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, raw, static_cast<int>(read), text.data(), wide);

        while (!text.empty() && (text.back() == L'\r' || text.back() == L'\n' || text.back() == L' '))
        {
            text.pop_back();
        }
        return text;
    }

    // 组件在 runtime\ 下时，程序本体在它的父目录
    std::wstring ParentDirectory()
    {
        const std::wstring directory = ModuleDirectory();
        const size_t slash = directory.find_last_of(L'\\');
        return slash == std::wstring::npos ? std::wstring{} : directory.substr(0, slash);
    }

    // 程序本体那份**真实文件**：组件同目录，其次父目录（组件收在 runtime\ 里时命中这一项）。
    // 启动与取图标都要用它，但要的正是它不同的性质 —— 打包形态下启动不能直接用它（见
    // PackagedAlias），取图标则非它不可（别名是重解析点，没有图标资源）。
    std::wstring AdjacentExecutable()
    {
        const wchar_t* const beside[] = { L"BullBullUnpacker.exe", L"BullBull Unpacker.exe", L"Bbu.App.exe" };
        for (const std::wstring& directory : { ModuleDirectory(), ParentDirectory() })
        {
            for (const wchar_t* name : beside)
            {
                const std::wstring candidate = directory + L"\\" + name;
                if (FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return {};
    }

    // 执行别名的全路径；没有这一份（没声明别名，或用户在「应用执行别名」里关掉了）时返回空。
    // 与 C# 侧 ShellMenu.PackagedAlias() 同一条口径，只是不查包身份：组件本身就在包里。
    std::wstring PackagedAlias()
    {
        wchar_t folder[MAX_PATH * 4]{};
        if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, folder)))
        {
            return {};
        }

        const std::wstring path =
            std::wstring(folder) + L"\\Microsoft\\WindowsApps\\" + BBU_ALIAS_FILE;
        return FileExists(path) ? path : std::wstring{};
    }

    // 启动谁：安装脚本写的指针最权威（稀疏包形态），其次是执行别名（打包形态），
    // 最后才是组件旁边的真实文件（便携形态）。
    // 落点只有 header 里那一处指针，别的来源没有读取方（B-2026-209）。
    std::wstring ResolveExecutable()
    {
        const std::wstring pointed = ReadTargetFromFile();
        if (!pointed.empty() && FileExists(pointed))
        {
            return pointed;
        }

        const std::wstring alias = PackagedAlias();
        if (!alias.empty())
        {
            return alias;
        }

        return AdjacentExecutable();
    }

    // ── 动作 ──

    // 每个路径启动一个进程，与老式注册键那三处同一条口径：合并交给主程序的单实例转发。
    bool Launch(const std::wstring& executable, const std::wstring& path)
    {
        std::wstring command = L"\"" + executable + L"\" \"" + path + L"\"";
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process{};

        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
                CREATE_UNICODE_ENVIRONMENT, nullptr, nullptr, &startup, &process))
        {
            return false;
        }

        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return true;
    }

    std::vector<std::wstring> PathsOf(IShellItemArray* items)
    {
        std::vector<std::wstring> paths;
        if (items == nullptr)
        {
            return paths;
        }

        DWORD count = 0;
        if (FAILED(items->GetCount(&count)))
        {
            return paths;
        }

        for (DWORD index = 0; index < count; ++index)
        {
            IShellItem* item = nullptr;
            if (FAILED(items->GetItemAt(index, &item)) || item == nullptr)
            {
                continue;
            }

            PWSTR name = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &name)) && name != nullptr)
            {
                paths.emplace_back(name);
                CoTaskMemFree(name);
            }
            item->Release();
        }

        return paths;
    }

    HRESULT LaunchAll(const std::vector<std::wstring>& paths)
    {
        if (paths.empty())
        {
            return S_OK;
        }

        const std::wstring executable = ResolveExecutable();
        if (executable.empty())
        {
            return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        }

        HRESULT result = S_OK;
        for (const std::wstring& path : paths)
        {
            if (!Launch(executable, path))
            {
                result = HRESULT_FROM_WIN32(GetLastError());
            }
        }
        return result;
    }

    // ── COM 对象 ──

    class BbuShellCommand final : public IExplorerCommand, public IContextMenu, public IShellExtInit
    {
    public:
        BbuShellCommand() { InterlockedIncrement(&g_objectCount); }

        // IUnknown
        IFACEMETHODIMP QueryInterface(REFIID riid, void** result) override
        {
            try
            {
                if (result == nullptr)
                {
                    return E_POINTER;
                }

                *result = nullptr;
                if (riid == IID_IUnknown || riid == IID_IExplorerCommand)
                {
                    *result = static_cast<IExplorerCommand*>(this);
                }
                else if (riid == IID_IContextMenu)
                {
                    *result = static_cast<IContextMenu*>(this);
                }
                else if (riid == IID_IShellExtInit)
                {
                    *result = static_cast<IShellExtInit*>(this);
                }
                else
                {
                    return E_NOINTERFACE;
                }

                AddRef();
                return S_OK;
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG left = InterlockedDecrement(&_refs);
            if (left == 0)
            {
                InterlockedDecrement(&g_objectCount);
                delete this;
            }
            return left;
        }

        // 菜单构造路径：不读文件内容、不遍历目录，只做常量与文件存在性判断；异常一律不越过 COM 边界（越过去会把宿主带崩）。
        IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override
        {
            try
            {
                if (name == nullptr)
                {
                    return E_POINTER;
                }

                *name = nullptr;
                return SHStrDupW(BBU_MENU_TEXT, name);
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        // 图标用程序本体自带的那一份：只判断文件是否存在，不读内容、不遍历目录。
        // 这里**不能**跟着 ResolveExecutable 走：打包形态下它给的是执行别名，那是个重解析点、
        // 本身没有图标资源，跟着走菜单项就成了空白图标（2026-10-14 真机）。
        IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
        {
            try
            {
                if (icon == nullptr)
                {
                    return E_POINTER;
                }

                *icon = nullptr;
                std::wstring executable = AdjacentExecutable();
                if (executable.empty())
                {
                    // 稀疏包形态下程序本体在包外，只有安装脚本的指针这一条来路
                    executable = ReadTargetFromFile();
                }

                if (executable.empty())
                {
                    return E_NOTIMPL;
                }

                return SHStrDupW((executable + L",0").c_str(), icon);
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tooltip) override
        {
            try
            {
                if (tooltip != nullptr)
                {
                    *tooltip = nullptr;
                }
                return E_NOTIMPL;
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        IFACEMETHODIMP GetCanonicalName(GUID* guid) override
        {
            try
            {
                if (guid == nullptr)
                {
                    return E_POINTER;
                }
                *guid = GUID_NULL;
                return S_OK;
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
        {
            try
            {
                if (state == nullptr)
                {
                    return E_POINTER;
                }

                // 方案 C：清单两段都声明，可见性由这里给 —— 界面那个开关关掉时回 ECS_HIDDEN。
                // 找不到程序本体时项仍在，由动作那一步报错（与老式菜单一致）。
                const bool modernOn = SwitchEnabled(BBU_SWITCH_MENU);
                *state = modernOn ? ECS_ENABLED : ECS_HIDDEN;
                return S_OK;
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
        {
            try
            {
                return LaunchAll(PathsOf(items));
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
        {
            try
            {
                if (flags == nullptr)
                {
                    return E_POINTER;
                }
                *flags = ECF_DEFAULT;
                return S_OK;
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
        {
            try
            {
                if (commands != nullptr)
                {
                    *commands = nullptr;
                }
                return E_NOTIMPL;
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        // IContextMenu
        IFACEMETHODIMP QueryContextMenu(
            HMENU menu, UINT index, UINT first, UINT last, UINT flags) override
        {
            if ((flags & CMF_DEFAULTONLY) != 0)
            {
                return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
            }
            if (first > last)
            {
                return E_INVALIDARG;
            }

            // 经典那一侧只在 Win10 及更早插项。Win11 上「显示更多选项」里已经有新式项，
            // 再插一份就是两个同名项 —— 而「查当前菜单有没有同名项」这个办法不能用：
            // 一级菜单构造时它也会被调用，那时新式项还没进那个 HMENU，查不到就把项插进了一级菜单
            // （2026-10-10 真机截图：一级菜单里一有一无两个同名项）。所以只看版本，不看菜单。
            const bool classicOn = SwitchEnabled(BBU_SWITCH_MENU);
            const bool needClassic = !IsWindows11OrGreater();
            if (!classicOn || !needClassic)
            {
                return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
            }

            if (!InsertMenuW(menu, index, MF_BYPOSITION | MF_STRING, first, BBU_MENU_TEXT))
            {
                return HRESULT_FROM_WIN32(GetLastError());
            }
            return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 1);
        }

        IFACEMETHODIMP InvokeCommand(LPCMINVOKECOMMANDINFO info) override
        {
            if (info == nullptr)
            {
                return E_POINTER;
            }
            if (!IS_INTRESOURCE(info->lpVerb) || LOWORD(reinterpret_cast<UINT_PTR>(info->lpVerb)) != 0)
            {
                return E_INVALIDARG;
            }

            std::vector<std::wstring> paths = PathsOf(_items);
            if (paths.empty() && !_folder.empty())
            {
                // 文件夹空白处：没有选中项，取 Initialize 给的那一层目录
                paths.push_back(_folder);
            }
            return LaunchAll(paths);
        }

        IFACEMETHODIMP GetCommandString(
            UINT_PTR id, UINT flags, UINT*, LPSTR name, UINT characters) override
        {
            if (id != 0 || name == nullptr)
            {
                return E_INVALIDARG;
            }

            if ((flags & GCS_VERBW) != 0)
            {
                return StringCchCopyW(reinterpret_cast<LPWSTR>(name), characters, BBU_VERB_NAME);
            }
            if ((flags & GCS_HELPTEXTW) != 0)
            {
                return StringCchCopyW(reinterpret_cast<LPWSTR>(name), characters, BBU_MENU_TEXT);
            }
            return E_NOTIMPL;
        }

        // IShellExtInit
        IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE folder, IDataObject* data, HKEY) override
        {
            if (_items != nullptr)
            {
                _items->Release();
                _items = nullptr;
            }
            _folder.clear();

            if (data != nullptr)
            {
                IShellItemArray* items = nullptr;
                if (SUCCEEDED(SHCreateShellItemArrayFromDataObject(data, IID_PPV_ARGS(&items))))
                {
                    _items = items;
                }
            }

            if (folder != nullptr)
            {
                wchar_t path[MAX_PATH * 4]{};
                if (SHGetPathFromIDListW(folder, path))
                {
                    _folder = path;
                }
            }
            return S_OK;
        }

    private:
        ~BbuShellCommand()
        {
            if (_items != nullptr)
            {
                _items->Release();
            }
        }

        LONG _refs = 1;
        IShellItemArray* _items = nullptr;
        std::wstring _folder;
    };

    class ClassFactory final : public IClassFactory
    {
    public:
        IFACEMETHODIMP QueryInterface(REFIID riid, void** result) override
        {
            if (result == nullptr)
            {
                return E_POINTER;
            }

            *result = nullptr;
            if (riid == IID_IUnknown || riid == IID_IClassFactory)
            {
                *result = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG left = InterlockedDecrement(&_refs);
            if (left == 0)
            {
                delete this;
            }
            return left;
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** result) override
        {
            if (result == nullptr)
            {
                return E_POINTER;
            }

            *result = nullptr;
            if (outer != nullptr)
            {
                return CLASS_E_NOAGGREGATION;
            }

            auto* command = new (std::nothrow) BbuShellCommand();
            if (command == nullptr)
            {
                return E_OUTOFMEMORY;
            }

            const HRESULT created = command->QueryInterface(riid, result);
            command->Release();
            return created;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock)
            {
                InterlockedIncrement(&g_objectCount);
            }
            else
            {
                InterlockedDecrement(&g_objectCount);
            }
            return S_OK;
        }

    private:
        ~ClassFactory() = default;
        LONG _refs = 1;
    };
}

// {4F2C9E31-7B5D-4A18-9C6E-3D8A1F5B7E20}
const CLSID CLSID_BbuShellExt =
{ 0x4f2c9e31, 0x7b5d, 0x4a18, { 0x9c, 0x6e, 0x3d, 0x8a, 0x1f, 0x5b, 0x7e, 0x20 } };

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** result)
{
    if (result == nullptr)
    {
        return E_POINTER;
    }

    *result = nullptr;
    if (clsid != CLSID_BbuShellExt)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) ClassFactory();
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    const HRESULT created = factory->QueryInterface(riid, result);
    factory->Release();
    return created;
}

STDAPI DllCanUnloadNow()
{
    return g_objectCount == 0 ? S_OK : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
