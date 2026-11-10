using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ZoomZoom;

namespace DwGangsta
{
    /// <summary>
    /// Выбор папки штатным диалогом Windows — тем же, что в проводнике: с адресной
    /// строкой, боковой панелью и полем «Папка», куда путь можно просто вставить.
    ///
    /// Старое окно «Обзор папок» (FolderBrowserDialog) такого поля не имеет: до нужной
    /// папки приходится докликиваться через всё дерево. Нового окна в Windows Forms
    /// под .NET Framework нет, поэтому оно берётся напрямую — через COM-интерфейс
    /// проводника. Если по какой-то причине не вышло, остаётся старое дерево.
    /// </summary>
    public static class FolderPicker
    {
        /// <summary>
        /// Показывает диалог. Возвращает выбранную папку или null, если отказались.
        /// </summary>
        /// <param name="owner">Окно-хозяин, чтобы диалог не ушёл за него.</param>
        /// <param name="start">Папка, с которой открыться; пустая строка — на усмотрение Windows.</param>
        /// <param name="title">Заголовок окна.</param>
        public static string Show(IWin32Window owner, string start, string title)
        {
            try
            {
                return ShowModern(owner, start, title);
            }
            catch (Exception ex)
            {
                Trace.Write("современный выбор папки недоступен: " + ex.Message);
                return ShowClassic(owner, start, title);
            }
        }

        static string ShowModern(IWin32Window owner, string start, string title)
        {
            var dialog = (IFileDialog)new FileOpenDialogRcw();
            try
            {
                // Окно обычное, файловое: чертежи в нём видно, и папка с ними не выглядит
                // пустой. А берём мы не файл, а папку, в которой стоим, — поэтому проверку
                // «файл должен существовать» снимаем: в строке имени стоит не файл.
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions((options | FosForceFileSystem | FosPathMustExist) & ~FosFileMustExist);

                dialog.SetFileTypes(1, new[]
                {
                    new FilterSpec { Name = Lang.T("Чертежи AutoCAD (*.dwg)", "AutoCAD drawings (*.dwg)"), Spec = "*.dwg" }
                });

                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                dialog.SetOkButtonLabel(Lang.T("Выбрать папку", "Select Folder"));
                dialog.SetFileNameLabel(Lang.T("Папка:", "Folder:"));

                // Открываемся там, где работали в прошлый раз: соседнюю папку ищут рядом.
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                {
                    IShellItem from = FromPath(start);
                    if (from != null) dialog.SetFolder(from);
                }

                // В строке внизу всегда стоит имя папки, в которой мы сейчас, — и видно,
                // что выберется. Без этого там висит имя файла с прошлого раза.
                var watcher = new FolderWatcher();
                uint cookie = 0;
                try { dialog.Advise(watcher, out cookie); }
                catch (Exception ex) { Trace.Write("слежение за папкой не включено: " + ex.Message); }

                IntPtr handle = owner != null ? owner.Handle : IntPtr.Zero;
                int shown;
                try
                {
                    shown = dialog.Show(handle);
                }
                finally
                {
                    if (cookie != 0) { try { dialog.Unadvise(cookie); } catch (Exception) { } }
                }

                if (shown == Cancelled) return null;
                if (shown != 0) throw new COMException("IFileDialog.Show", shown);

                IShellItem chosen;
                dialog.GetResult(out chosen);
                if (chosen == null) return null;

                string path;
                chosen.GetDisplayName(SigdnFileSysPath, out path);
                Marshal.ReleaseComObject(chosen);

                // В руках у нас путь вида «папка\Эта папка» — либо, если ткнули в чертёж,
                // путь самого чертежа. И в том, и в другом случае нужна его папка.
                if (Directory.Exists(path)) return path;
                return Path.GetDirectoryName(path);
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        /// <summary>Запасной путь: старое дерево папок. Работает везде и всегда.</summary>
        static string ShowClassic(IWin32Window owner, string start, string title)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = title;
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dialog.SelectedPath = start;
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        static IShellItem FromPath(string path)
        {
            try
            {
                Guid id = IidShellItem;
                object item;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref id, out item);
                return item as IShellItem;
            }
            catch (Exception ex)
            {
                Trace.Write("стартовая папка не задана: " + ex.Message);
                return null;
            }
        }

        // --- то, что нужно знать проводнику ----------------------------------------
        const uint FosForceFileSystem = 0x00000040;    // только настоящие папки на диске
        const uint FosPathMustExist = 0x00000800;
        const uint FosFileMustExist = 0x00001000;      // снимаем: берём папку, а не файл

        const uint SigdnFileSysPath = 0x80058000;      // отдать путь, а не красивое имя
        const uint SigdnNormalDisplay = 0x00000000;    // имя, как его показывает проводник
        const int Cancelled = unchecked((int)0x800704C7);

        static readonly Guid IidShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context, ref Guid id,
            [MarshalAs(UnmanagedType.Interface)] out object item);

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        class FileOpenDialogRcw { }

        /// <summary>Строка фильтра окна: что показывать в списке.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FilterSpec
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string Name;
            [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
        }

        /// <summary>
        /// Интерфейс диалога. Методы обязаны идти ровно в том порядке, в каком их знает
        /// Windows, поэтому неиспользуемые объявлены пустышками — они только держат место
        /// и никогда не вызываются.
        /// </summary>
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);

            void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] filters);
            void SetFileTypeIndex();
            void GetFileTypeIndex();
            void Advise(IFileDialogEvents events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName();
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace();
            void SetDefaultExtension();
            void Close();
            void SetClientGuid();
            void ClearClientData();
            void SetFilter();
        }

        /// <summary>События окна. Нужно одно — смену папки.</summary>
        [ComImport, Guid("973510db-7d7f-452b-8975-74a85828d354"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialogEvents
        {
            [PreserveSig] int OnFileOk(IFileDialog dialog);
            [PreserveSig] int OnFolderChanging(IFileDialog dialog, IShellItem folder);
            [PreserveSig] int OnFolderChange(IFileDialog dialog);
            [PreserveSig] int OnSelectionChange(IFileDialog dialog);
            [PreserveSig] int OnShareViolation(IFileDialog dialog, IShellItem item, out int answer);
            [PreserveSig] int OnTypeChange(IFileDialog dialog);
            [PreserveSig] int OnOverwrite(IFileDialog dialog, IShellItem item, out int answer);
        }

        /// <summary>
        /// Держит в строке внизу имя той папки, в которую зашли. Окно файловое, и строка
        /// там есть всегда — пусть она говорит ровно то, что выберется по кнопке.
        /// </summary>
        class FolderWatcher : IFileDialogEvents
        {
            public int OnFolderChange(IFileDialog dialog)
            {
                try
                {
                    IShellItem folder;
                    dialog.GetFolder(out folder);
                    if (folder == null) return 0;

                    string name;
                    folder.GetDisplayName(SigdnNormalDisplay, out name);
                    Marshal.ReleaseComObject(folder);

                    if (!string.IsNullOrEmpty(name)) dialog.SetFileName(name);
                }
                catch (Exception ex)
                {
                    Trace.Write("имя папки в строке не обновлено: " + ex.Message);
                }
                return 0;
            }

            public int OnFileOk(IFileDialog dialog) { return 0; }
            public int OnFolderChanging(IFileDialog dialog, IShellItem folder) { return 0; }
            public int OnSelectionChange(IFileDialog dialog) { return 0; }
            public int OnTypeChange(IFileDialog dialog) { return 0; }

            public int OnShareViolation(IFileDialog dialog, IShellItem item, out int answer)
            {
                answer = 0;
                return 0;
            }

            public int OnOverwrite(IFileDialog dialog, IShellItem item, out int answer)
            {
                answer = 0;
                return 0;
            }
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler();
            void GetParent();
            void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes();
            void Compare();
        }
    }
}
