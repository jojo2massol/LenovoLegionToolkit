using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// Custom mode fan curve for Legion 5 Gen 6 AMD (BIOS G9CN, ITE EC 0x8227). Its BIOS leaves LENOVO_FAN_METHOD
/// Fan_Set_Table unimplemented and LENOVO_FAN_TABLE_DATA empty, so the curve goes straight into the EC RAM through
/// ITE I2EC (SuperIO 0x4E/0x4F) with PawnIO's LpcIO module, like the legion-laptop Linux driver.
/// Point 0 is always written as 0 RPM: a non-zero point 0 hangs this EC firmware (platform reset ~20 s later).
/// </summary>
public static class LegacyFanTableEC
{
    private const string BIOS_PREFIX = "G9CN";
    private const int EC_CHIP_ID = 0x8227;
    private const ushort EC_CHIP_ID_ADDRESS = 0x2000;
    private const ushort FAN1_TABLE_ADDRESS = 0xC540;
    private const ushort FAN2_TABLE_ADDRESS = 0xC550;

    // EC's own Custom mode curve (x100 RPM) and the temperatures entering each point (cpu_max/gpu_max shifted by one)
    private static readonly byte[] Speeds = [0, 17, 19, 21, 23, 26, 29, 35, 42, 45];
    private static readonly ushort[] CpuTemps = [0, 56, 56, 56, 60, 66, 72, 80, 87, 90];
    private static readonly ushort[] GpuTemps = [0, 65, 65, 65, 65, 65, 65, 70, 78, 82];

    public static async Task<bool> IsSupportedAsync()
    {
        var mi = await Compatibility.GetMachineInformationAsync().ConfigureAwait(false);
        return BIOS_PREFIX.Equals(mi.BiosVersion?.Prefix, StringComparison.InvariantCultureIgnoreCase)
               && PawnIOHelper.GetPawnIOState() == PawnIOState.Installed;
    }

    public static FanTableData[] GetFanTableData()
    {
        var fanSpeeds = Speeds.Select(s => (ushort)(s * 100)).ToArray();
        return
        [
            new(FanTableType.CPU, 0, 3, fanSpeeds, CpuTemps),
            new(FanTableType.GPU, 1, 4, fanSpeeds, GpuTemps),
        ];
    }

    public static async Task SetFanTableAsync(FanTable fanTable)
    {
        var levels = fanTable.GetTable();
        var speeds = levels.Select((level, i) => i == 0 || level == 0 ? (byte)0 : Speeds[level - 1]).ToArray();
        const int maxAttempts = 3;

        using var ec = new I2EC();
        ec.Run(() =>
        {
            var chipId = ec.Read(EC_CHIP_ID_ADDRESS) << 8 | ec.Read(EC_CHIP_ID_ADDRESS + 1);
            if (chipId != EC_CHIP_ID)
                throw new InvalidOperationException($"Unexpected EC chip ID 0x{chipId:X4}.");
        });

        // The EC reloads its own table ~0.3-0.4 s after a power mode change, possibly after our write: check and rewrite
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var written = ec.Run(() =>
            {
                if (IsWritten(ec, speeds))
                    return false;

                Log.Instance.Trace($"Writing fan table to EC (attempt {attempt}): [{string.Join(", ", speeds)}]");
                for (var i = 0; i < speeds.Length; i++)
                {
                    ec.Write(FAN1_TABLE_ADDRESS + i, speeds[i]);
                    ec.Write(FAN2_TABLE_ADDRESS + i, speeds[i]);
                }
                return true;
            });

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            if (ec.Run(() => IsWritten(ec, speeds)))
            {
                Log.Instance.Trace($"EC fan table verified. [written={written}, attempt={attempt}]");
                return;
            }
        }

        throw new InvalidOperationException("EC fan table keeps being overwritten.");
    }

    private static bool IsWritten(I2EC ec, byte[] speeds) =>
        speeds.Select((s, i) => ec.Read(FAN1_TABLE_ADDRESS + i) == s && ec.Read(FAN2_TABLE_ADDRESS + i) == s).All(ok => ok);

    private sealed class I2EC : IDisposable
    {
        private const string ISA_BUS_MUTEX = @"Global\Access_ISABUS.HTP.Method";
        private const string LPC_IO_RESOURCE = "LibreHardwareMonitor.Resources.PawnIo.LpcIO.bin";

        private delegate int PawnIOOpen(out IntPtr handle);
        private delegate int PawnIOLoad(IntPtr handle, byte[] blob, nint size);
        private delegate int PawnIOExecute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name,
            ulong[] input, nint inputSize, ulong[] output, nint outputSize, out nint returnSize);
        private delegate int PawnIOClose(IntPtr handle);

        private readonly IntPtr _library;
        private readonly IntPtr _handle;
        private readonly PawnIOExecute _execute;
        private readonly PawnIOClose _close;
        private readonly Mutex _isaBusMutex = new(false, ISA_BUS_MUTEX);

        public I2EC()
        {
            _library = NativeLibrary.Load(Path.Combine(PawnIOHelper.GetInstallPath()!, "PawnIOLib.dll"));
            _execute = Export<PawnIOExecute>("pawnio_execute");
            _close = Export<PawnIOClose>("pawnio_close");

            Check(Export<PawnIOOpen>("pawnio_open")(out _handle), "pawnio_open");

            // LpcIO is the signed module LibreHardwareMonitor already ships for SuperIO access
            using var stream = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetManifestResourceStream(LPC_IO_RESOURCE)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var blob = ms.ToArray();
            Check(Export<PawnIOLoad>("pawnio_load")(_handle, blob, blob.Length), "pawnio_load");

            Execute("ioctl_select_slot", 1); // SuperIO slot 1 = ports 0x4E/0x4F
        }

        public void Run(Action action) => Run(() =>
        {
            action();
            return true;
        });

        public T Run<T>(Func<T> func)
        {
            if (!_isaBusMutex.WaitOne(TimeSpan.FromSeconds(2)))
                throw new TimeoutException("ISA bus mutex is busy.");

            try
            {
                return func();
            }
            finally
            {
                _isaBusMutex.ReleaseMutex();
            }
        }

        public byte Read(int address)
        {
            SetAddress(address);
            return (byte)Execute("ioctl_superio_inb", 0x2F);
        }

        public void Write(int address, byte value)
        {
            SetAddress(address);
            Execute("ioctl_superio_outb", 0x2F, value);
        }

        public void Dispose()
        {
            _close(_handle);
            NativeLibrary.Free(_library);
            _isaBusMutex.Dispose();
        }

        private void SetAddress(int address)
        {
            Execute("ioctl_superio_outb", 0x2E, 0x11);
            Execute("ioctl_superio_outb", 0x2F, (ulong)(address >> 8) & 0xFF);
            Execute("ioctl_superio_outb", 0x2E, 0x10);
            Execute("ioctl_superio_outb", 0x2F, (ulong)address & 0xFF);
            Execute("ioctl_superio_outb", 0x2E, 0x12);
        }

        private ulong Execute(string name, params ulong[] input)
        {
            var output = new ulong[1];
            Check(_execute(_handle, name, input, input.Length, output, output.Length, out _), name);
            return output[0];
        }

        private T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

        private static void Check(int hresult, string name)
        {
            if (hresult != 0)
                throw new InvalidOperationException($"PawnIO {name} failed. [hresult=0x{hresult:X8}]");
        }
    }
}
