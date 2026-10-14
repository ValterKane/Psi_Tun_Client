using System.IO;

namespace PsiTun.Services;

/// <summary>
/// Дневной файл журнала. Имя — по дате; файл создаётся сам на первой строке дня,
/// поэтому смена даты посреди работы открывает следующий файл без отдельного
/// состояния: «открытого» файла в классе нет вовсе.
///
/// Запись сериализована — строки приходят из потока таймера интерфейса, из
/// потока ядра и из фоновых задач. Отказ записи не поднимается наверх: журнал
/// не должен ронять работу, ради которой пишется.
/// </summary>
public static class LogFile
{
    private static readonly object Gate = new();

    /// <summary>
    /// Имя файла дня. Одно правило на все случаи — и на запись, и на проверку
    /// смены даты в тесте: раздвоенное правило разошлось бы молча.
    /// </summary>
    public static string FileNameFor(DateTime moment) => $"{moment:yyyy-MM-dd}.log";

    /// <summary>
    /// Дописать строку в файл дня. Момент передаётся вызывающим, а не берётся
    /// здесь: иначе смену даты нельзя проверить, не переводя часы машины.
    /// </summary>
    public static void Append(string directory, string line, DateTime moment)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, FileNameFor(moment)),
                    $"{moment:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch { /* журнал не роняет работу */ }
    }
}
