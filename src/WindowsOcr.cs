using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace TextOutline
{
    // Reads text from an image with the OCR engine built into Windows 10/11 (Windows.Media.Ocr).
    // It runs through Windows PowerShell 5.1, which can call that engine on every Windows 10/11 PC,
    // so the same code works in AutoCAD 2020 (.NET Framework) and AutoCAD 2025+ (.NET 8/10).
    public static class WindowsOcr
    {
        const string Script = @"
param([string]$ImagePath, [string]$Language)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } |
    Select-Object -First 1
function Wait-Op($op, [Type]$type) {
    $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
    $null = $task.Wait(-1)
    $task.Result
}
$file = Wait-Op ([Windows.Storage.StorageFile]::GetFileFromPathAsync($ImagePath)) ([Windows.Storage.StorageFile])
$stream = Wait-Op ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Wait-Op ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Wait-Op ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$engine = $null
if ($Language) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new($Language)) }
if (-not $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
if (-not $engine) { Write-Output 'ERROR: no OCR language installed'; exit 2 }
$result = Wait-Op ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
foreach ($line in $result.Lines) { Write-Output ('LINE ' + $line.Text) }
$stream.Dispose()
";

        public static string LastError = "";

        // Returns the recognised lines (empty if nothing was read or OCR is not available).
        public static List<string> Recognize(Bitmap image, string language)
        {
            List<string> lines = new List<string>();
            LastError = "";
            string dir = Path.Combine(Path.GetTempPath(), "TextOutline");
            Directory.CreateDirectory(dir);
            string id = Guid.NewGuid().ToString("N");
            string png = Path.Combine(dir, id + ".png"), ps1 = Path.Combine(dir, "ocr.ps1");
            try
            {
                image.Save(png, ImageFormat.Png);
                File.WriteAllText(ps1, Script, new UTF8Encoding(true));

                string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                          @"WindowsPowerShell\v1.0\powershell.exe");
                if (!File.Exists(exe)) exe = "powershell.exe";
                ProcessStartInfo psi = new ProcessStartInfo(exe,
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + ps1 + "\" -ImagePath \"" + png + "\"" +
                    (string.IsNullOrEmpty(language) ? "" : " -Language \"" + language + "\""));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;

                using (Process p = Process.Start(psi))
                {
                    string err = "";
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) err += e.Data + " "; };
                    p.BeginErrorReadLine();
                    string outText = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch (Exception) { } LastError = "OCR timed out"; return lines; }
                    foreach (string raw in outText.Split('\n'))
                    {
                        string l = raw.TrimEnd('\r');
                        if (l.StartsWith("LINE ")) lines.Add(l.Substring(5).Trim());
                        else if (l.StartsWith("ERROR: ")) LastError = l.Substring(7);
                    }
                    if (lines.Count == 0 && LastError.Length == 0 && err.Trim().Length > 0) LastError = err.Trim();
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                try { File.Delete(png); } catch (Exception) { }
            }
            return lines;
        }

        // Counts the letters and digits in OCR output: used to pick the better of two orientations.
        public static int Quality(List<string> lines)
        {
            int n = 0;
            foreach (string l in lines)
                foreach (char c in l)
                    if (char.IsLetterOrDigit(c)) n++;
            return n;
        }
    }
}
