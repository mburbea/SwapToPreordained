using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SwapToPreordained;

internal sealed class Program : IDisposable
{
    private static readonly string ScratchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private static readonly string KsmtBatch = Path.Combine(ScratchDir, "134225858_ksmt.batch");

    private readonly Dictionary<int, string> _simTypes = new();
    private readonly Dictionary<string, int> _fabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _fabSlots = new();
    private static void BackupOrRestore()
    {
        if (!File.Exists(@"..\data\patch_0_po.zip"))
        {
            using var zip = new ZipArchive(File.OpenWrite(@"..\data\patch_0_po.zip"), ZipArchiveMode.Create);
            zip.CreateEntryFromFile(@"..\data\patch_0.pak", "patch_0.pak");
        }
        else
        {
            File.Delete(@"..\data\patch_0.pak");
            ZipFile.ExtractToDirectory(@"..\data\patch_0_po.zip", @"..\data\");
        }
    }

    private static IEnumerable<KeyValuePair<int, string>> ConvertSymbolTableToDictionary(string bin)
    {
        var data = File.ReadAllBytes(bin);
        var elementCount = BitConverter.ToInt32(data, 0);
        var firstString = 8 + elementCount * 12;
        return Enumerable.Range(0, elementCount)
            .Select(x => Unsafe.ReadUnaligned<IntTriplet>(ref data[4 + 12 * x]))
            .Select(x =>
            {
                var (id, start, end) = x;
                if (data[firstString + end - 1] == 0)
                {
                    end--;
                }
                return KeyValuePair.Create(id, Encoding.UTF8.GetString(data[(firstString + start)..(firstString + end)]));
            });
    }

    private static void Unpack()
    {
        Process.Start("pakfileunpacker.exe", @$"..\data\patch_0.pak unpack {ScratchDir}").WaitForExit();
        Process.Start("pakfileunpacker.exe", $@"..\data\initial_0.pak unpack {ScratchDir} symbol_table_fabslot.bin").WaitForExit();
    }

    private static void Pack()
    {
        var listPath = Path.GetTempFileName();
        File.WriteAllLines(listPath, Directory.EnumerateFiles(ScratchDir));
        Process.Start("pakfilebuilder.exe", @$"-c {listPath} ..\data\patch_0.pak").WaitForExit();
        File.Delete(listPath);
    }

    public Program()
    {
        Directory.SetCurrentDirectory(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        if (Debugger.IsAttached)
        {
            Directory.SetCurrentDirectory(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Steam\steamapps\common\Kingdoms of Amalur Re-Reckoning\modding\")));
        }
        Directory.CreateDirectory(ScratchDir);
    }

    public static void Main()
    {
        using var runner = new Program();
        if (!File.Exists("pakfileunpacker.exe"))
        {
            Console.WriteLine("pakfileunpacker.exe not found. Please make sure this is located in your Kingdoms of Amalur Re-Reckoning\\modding directory. Press any key to exit");
            Console.ReadKey();
            Environment.Exit(1);
        }
        runner.Run();
    }

    private void Run()
    {
        BackupOrRestore();
        Unpack();
        BuildDictionaries();
        ModifySimtypes();
        Pack();
    }

    private const string MagePrefix = "mit_mage_common06";
    private const string RoguePrefix = "mit_rogue_common06";
    private const string WarriorPrefix = "mit_warrior_common07";

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct IntPair(int Id, int Size);
    [StructLayout(LayoutKind.Sequential, Size = 12, Pack = 1)]
    private readonly record struct IntTriplet(int Id, int Start, int End);

    private (int, int) GetFabIds(string category, string prefix) => prefix switch
    {
        MagePrefix => (_fabs[$"mit_preordained_mage_{category}_01"], _fabs[$"mit_preordained_mage_f_{category}_01"]),
        RoguePrefix when category.SequenceEqual("head") => (_fabs[$"mit_preordained_helmet_01"], _fabs[$"mit_preordained_f_helmet_01"]),
        RoguePrefix => (_fabs[$"mit_preordained_{category}_01"], _fabs[$"mit_preordained_f_{category}_01"]),
        _ => (_fabs[$"me_armor_{category}"], _fabs[$"me_armor_female_{category}"])
    };

    private void ModifySimtypes()
    {
        var data = File.ReadAllBytes(KsmtBatch);
        int entryCount = Unsafe.ReadUnaligned<int>(ref data[0]);
        var assets = MemoryMarshal.Cast<byte, IntPair>(data.AsSpan(4, entryCount * Unsafe.SizeOf<IntPair>()));
        var remaining = data.AsSpan((sizeof(int) + entryCount * sizeof(ulong))..);
        foreach (var (id, fileSize) in assets)
        {
            string prefix;
            if (_simTypes.GetValueOrDefault(id) is { } name
                && (name.StartsWith(prefix = MagePrefix) 
                || name.StartsWith(prefix = RoguePrefix) 
                || name.StartsWith(prefix = WarriorPrefix)))
            {
                var simtype = remaining[..fileSize];
                var category = name.AsSpan(prefix.Length + 2);
                var usix = category.IndexOf('_');
                category = usix == -1 ? category : category[..usix];
                var (maleId, femaleId) = GetFabIds(category.ToString(), prefix);
                var ix = simtype.IndexOf(new byte[] { 0x06, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00 });
                var (oldMaleId, oldFemaleId) = Unsafe.ReadUnaligned<IntPair>(ref simtype[ix + 28]);
                Unsafe.WriteUnaligned(ref simtype[ix + 28], maleId);
                Unsafe.WriteUnaligned(ref simtype[ix + 32], femaleId);
                if (category.SequenceEqual("head") && prefix == RoguePrefix)
                {
                    Unsafe.WriteUnaligned(ref simtype[ix + 48], _fabSlots["HairAndHelmet"]);
                }
                var bundlePath = Path.Combine(ScratchDir, $"{id}.bundle");
                var bundleBytes = new byte[30];
                Unsafe.WriteUnaligned(ref bundleBytes[8], 2);
                Unsafe.WriteUnaligned(ref bundleBytes[16], maleId);
                Unsafe.WriteUnaligned(ref bundleBytes[20], femaleId);
                Unsafe.WriteUnaligned(ref bundleBytes[24], 0x00_00_10_10);
                File.WriteAllBytes(bundlePath, bundleBytes);
            }
            remaining = remaining[fileSize..];

        }
        File.WriteAllBytes(KsmtBatch, data);
    }

    private void BuildDictionaries()
    {
        _simTypes.AddRange(ConvertSymbolTableToDictionary(Path.Combine(ScratchDir, "symbol_table_simtype.bin")));
        _fabs.AddRange(ConvertSymbolTableToDictionary(Path.Combine(ScratchDir, "symbol_table_fab.bin")).Select(kvp => KeyValuePair.Create(kvp.Value, kvp.Key)));
        _fabSlots.AddRange(ConvertSymbolTableToDictionary(Path.Combine(ScratchDir, "symbol_table_fabslot.bin")).Select(kvp => KeyValuePair.Create(kvp.Value, kvp.Key)));

    }

    public void Dispose() => Directory.Delete(ScratchDir, true);
}

internal static class Extensions
{
    public static void AddRange<T>(this ICollection<T> collection, IEnumerable<T> toAdd)
    {
        foreach (var item in toAdd)
        {
            collection.Add(item);
        }
    }
}