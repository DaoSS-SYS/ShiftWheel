using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShiftWheel;

/// <summary>
/// Word / PowerPoint / Excel conversions through the user's own Microsoft Office (COM automation).
/// Without Office installed these formats are reported as unavailable.
/// </summary>
internal static class OfficeTools
{
    public static Task<string[]> ConvertAsync(string input, FileKind kind, string target, IProgress<double>? progress)
    {
        return OnStaThread(() => kind switch
        {
            FileKind.Word => ConvertWord(input, target),
            FileKind.Slides => ConvertSlides(input, target, progress),
            FileKind.Sheet => ConvertSheet(input, target),
            _ => throw new ConvertException("Неподдерживаемая операция"),
        });
    }

    // ───────────── Word ─────────────

    static string[] ConvertWord(string input, string target)
    {
        string output = Converter.UniquePath(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input), target);
        using var app = OfficeApp.Start("Word.Application", "WINWORD", "Microsoft Word");
        dynamic doc = app.Dyn.Documents.Open(FileName: input, ReadOnly: true, AddToRecentFiles: false, Visible: false);
        try
        {
            switch (target)
            {
                case "pdf": doc.SaveAs2(FileName: output, FileFormat: 17); break;           // wdFormatPDF
                case "docx": doc.SaveAs2(FileName: output, FileFormat: 16); break;          // wdFormatXMLDocument
                case "txt": doc.SaveAs2(FileName: output, FileFormat: 2, Encoding: 65001); break; // wdFormatText, UTF-8
                default: throw new ConvertException($"Формат {target.ToUpperInvariant()} для Word не поддерживается");
            }
        }
        catch (COMException ex) { throw Wrap(ex); }
        finally { TryCall(() => doc.Close(0)); }
        return new[] { output };
    }

    // ───────────── PowerPoint ─────────────

    static string[] ConvertSlides(string input, string target, IProgress<double>? progress)
    {
        string dir = Path.GetDirectoryName(input)!;
        string name = Path.GetFileNameWithoutExtension(input);
        using var app = OfficeApp.Start("PowerPoint.Application", "POWERPNT", "Microsoft PowerPoint");
        dynamic pres = app.Dyn.Presentations.Open(FileName: input, ReadOnly: -1, Untitled: 0, WithWindow: 0);
        try
        {
            if (target == "pdf")
            {
                string output = Converter.UniquePath(dir, name, "pdf");
                pres.SaveAs(FileName: output, FileFormat: 32);                               // ppSaveAsPDF
                return new[] { output };
            }

            // PNG / JPG: one image per slide
            int count = pres.Slides.Count;
            var outputs = new List<string>();
            for (int i = 1; i <= count; i++)
            {
                string file = Converter.UniquePath(dir, count == 1 ? name : $"{name} - slide {i}", target);
                pres.Slides.Item(i).Export(file, target.ToUpperInvariant(), 1920, 1080);
                outputs.Add(file);
                progress?.Report((double)i / count);
            }
            return outputs.ToArray();
        }
        catch (COMException ex) { throw Wrap(ex); }
        finally { TryCall(() => pres.Close()); }
    }

    // ───────────── Excel ─────────────

    static string[] ConvertSheet(string input, string target)
    {
        string output = Converter.UniquePath(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input), target);
        using var app = OfficeApp.Start("Excel.Application", "EXCEL", "Microsoft Excel");
        dynamic book = app.Dyn.Workbooks.Open(Filename: input, ReadOnly: true);
        try
        {
            switch (target)
            {
                case "pdf": book.ExportAsFixedFormat(0, output); break;                      // xlTypePDF
                case "xlsx": book.SaveAs(Filename: output, FileFormat: 51); break;           // xlOpenXMLWorkbook
                case "csv": book.SaveAs(Filename: output, FileFormat: 62); break;            // xlCSVUTF8 (first sheet)
                default: throw new ConvertException($"Формат {target.ToUpperInvariant()} для Excel не поддерживается");
            }
        }
        catch (COMException ex) { throw Wrap(ex); }
        finally { TryCall(() => book.Close(false)); }
        return new[] { output };
    }

    // ───────────── helpers ─────────────

    static ConvertException Wrap(COMException ex) =>
        new("Ошибка Microsoft Office: " + ex.Message.Trim());

    static void TryCall(Action a)
    {
        try { a(); } catch { }
    }

    /// <summary>COM automation of Office wants a single-threaded apartment.</summary>
    static Task<T> OnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// An Office application started for one conversion. It is only quit afterwards when we started it
    /// ourselves, so a Word/PowerPoint/Excel the user already has open is never closed under them.
    /// </summary>
    sealed class OfficeApp : IDisposable
    {
        public dynamic Dyn { get; }
        readonly bool _weStartedIt;

        OfficeApp(dynamic app, bool weStartedIt)
        {
            Dyn = app;
            _weStartedIt = weStartedIt;
        }

        public static OfficeApp Start(string progId, string processName, string title)
        {
            var type = Type.GetTypeFromProgID(progId)
                ?? throw new ConvertException($"Для этого нужен {title}, он не найден на компьютере");
            bool wasRunning = Process.GetProcessesByName(processName).Length > 0;
            try
            {
                dynamic app = Activator.CreateInstance(type)!;
                TryCall(() => app.DisplayAlerts = false);
                return new OfficeApp(app, !wasRunning);
            }
            catch (Exception ex)
            {
                throw new ConvertException($"Не удалось запустить {title}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_weStartedIt) TryCall(() => Dyn.Quit());
            TryCall(() => Marshal.FinalReleaseComObject(Dyn));
        }
    }
}
