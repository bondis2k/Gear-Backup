// Windows PTP transport for settings capture. The only write allowed is the temporary recipe
// read selector (D18C); no setting value, image, capture or reset command can be sent here.
using System;
using System.IO;
using System.Runtime.InteropServices;
using static GearBackup;
using static FujiSettings;
using static PortableDevices;

[ComImport, Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropVariantCollection
{
    void GetCount(out uint count);
    void GetAt(uint index, IntPtr value);
    void Add(IntPtr value);
    void GetType(out ushort type);
    void ChangeType(ushort type);
    void Clear();
    void RemoveAt(uint index);
}

internal sealed class FujiPtp : IFujiSettingsSource, IDisposable
{
    static readonly Guid Category = new Guid("4D545058-1A2E-4106-A357-771E0819FC56");
    static readonly Guid Common = new Guid("F0422A9C-5DC8-4440-B5BD-5DF28835658A");
    const int MaximumPayload = 1024 * 1024;
    IPortableDevice device;

    public FujiPtp(string interfacePath)
    {
        device = (IPortableDevice)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("F7C0039A-4762-488A-B4B3-760EF9A1BA9B")));
        IPortableDeviceValues client = Values();
        try { device.Open(interfacePath, client); }
        catch { Release(device); device = null; throw; }
        finally { Release(client); }
    }

    public void Dispose()
    {
        if (device == null) return;
        try { device.Close(); }
        finally { Release(device); device = null; }
    }

    public byte[] DeviceInfo() { return Read(0x1001, new uint[0]); }
    public byte[] ReadProperty(ushort code) { return Read(0x1015, new uint[] { code }); }

    public void SelectRecipe(ushort slot)
    {
        if (slot > 7) throw new IOException("Invalid X100VI recipe selector.");
        IPortableDeviceValues start = Operation(14, 0x1016, new uint[] { 0xD18C });
        PropertyKey size = Key(1007), contextKey = Key(1006);
        string context = null;
        try
        {
            start.SetUnsignedLargeIntegerValue(ref size, 2);
            IPortableDeviceValues result = Send(start);
            try { result.GetStringValue(ref contextKey, out context); }
            finally { Release(result); }
            IPortableDeviceValues write = Command(16);
            try
            {
                write.SetStringValue(ref contextKey, context);
                PropertyKey number = Key(1010), data = Key(1012);
                write.SetUnsignedIntegerValue(ref number, 2);
                SetBuffer(write, data, new byte[] { (byte)slot, 0 });
                result = Send(write);
                try { if (Unsigned(result, 1011) != 2) throw new IOException("Incomplete recipe selection."); }
                finally { Release(result); }
            }
            finally { Release(write); }
        }
        finally { Release(start); if (context != null) End(context); }
        // Allow the camera to switch its read bank before asking for values. Restoration does
        // not consult cancellation, so a cancelled capture still returns to the original bank.
        System.Threading.Thread.Sleep(100);
    }

    byte[] Read(uint operation, uint[] parameters)
    {
        IPortableDeviceValues start = Operation(13, operation, parameters);
        string context = null;
        PropertyKey contextKey = Key(1006), sizeKey = Key(1007);
        try
        {
            IPortableDeviceValues result = Send(start);
            ulong size;
            try
            {
                result.GetStringValue(ref contextKey, out context);
                result.GetUnsignedLargeIntegerValue(ref sizeKey, out size);
            }
            finally { Release(result); }
            if (size > MaximumPayload) throw new IOException("Camera settings response exceeds the size limit.");
            using (var output = new MemoryStream())
            {
                while ((ulong)output.Length < size)
                {
                    uint wanted = (uint)Math.Min(16384, (long)size - output.Length);
                    IPortableDeviceValues read = Command(15);
                    try
                    {
                        read.SetStringValue(ref contextKey, context);
                        PropertyKey number = Key(1008), dataKey = Key(1012);
                        read.SetUnsignedIntegerValue(ref number, wanted);
                        SetBuffer(read, dataKey, new byte[wanted]);
                        result = Send(read);
                        try
                        {
                            uint actual = Unsigned(result, 1009), length;
                            IntPtr data;
                            result.GetBufferValue(ref dataKey, out data, out length);
                            try
                            {
                                if (actual == 0 || actual > wanted || actual > length)
                                    throw new IOException("Incomplete camera settings response.");
                                byte[] bytes = new byte[actual];
                                Marshal.Copy(data, bytes, 0, bytes.Length);
                                output.Write(bytes, 0, bytes.Length);
                            }
                            finally { Marshal.FreeCoTaskMem(data); }
                        }
                        finally { Release(result); }
                    }
                    finally { Release(read); }
                }
                return output.ToArray();
            }
        }
        finally { Release(start); if (context != null) End(context); }
    }

    void End(string context)
    {
        IPortableDeviceValues command = Command(17);
        try
        {
            PropertyKey key = Key(1006);
            command.SetStringValue(ref key, context);
            IPortableDeviceValues result = Send(command);
            try
            {
                uint response = Unsigned(result, 1003);
                if (response != 0x2001) throw new FujiPtpException(response);
            }
            finally { Release(result); }
        }
        finally { Release(command); }
    }

    IPortableDeviceValues Send(IPortableDeviceValues command)
    {
        IntPtr request = Marshal.GetIUnknownForObject(command), response = IntPtr.Zero;
        IPortableDeviceValues result = null;
        try
        {
            device.SendCommand(0, request, out response);
            result = (IPortableDeviceValues)Marshal.GetObjectForIUnknown(response);
            PropertyKey error = new PropertyKey(Common, 1003);
            int hr;
            result.GetErrorValue(ref error, out hr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return result;
        }
        catch { Release(result); throw; }
        finally { Marshal.Release(request); if (response != IntPtr.Zero) Marshal.Release(response); }
    }

    static IPortableDeviceValues Operation(uint command, uint operation, uint[] parameters)
    {
        IPortableDeviceValues values = Command(command);
        IPortableDevicePropVariantCollection collection = null;
        try
        {
            PropertyKey code = Key(1001), args = Key(1002);
            values.SetUnsignedIntegerValue(ref code, operation);
            collection = (IPortableDevicePropVariantCollection)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("08a99e2f-6d6d-4b80-af5a-baf2bcbe4cb9")));
            IntPtr variant = Marshal.AllocCoTaskMem(24);
            try
            {
                for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
                Marshal.WriteInt16(variant, 19); // VT_UI4
                foreach (uint parameter in parameters)
                {
                    Marshal.WriteInt32(variant, 8, unchecked((int)parameter));
                    collection.Add(variant);
                }
            }
            finally { Marshal.FreeCoTaskMem(variant); }
            IntPtr pointer = Marshal.GetIUnknownForObject(collection);
            try { values.SetIPortableDevicePropVariantCollectionValue(ref args, pointer); }
            finally { Marshal.Release(pointer); }
            return values;
        }
        catch { Release(values); throw; }
        finally { Release(collection); }
    }

    static void SetBuffer(IPortableDeviceValues values, PropertyKey key, byte[] bytes)
    {
        IntPtr buffer = Marshal.AllocCoTaskMem(bytes.Length);
        try { Marshal.Copy(bytes, 0, buffer, bytes.Length); values.SetBufferValue(ref key, buffer, (uint)bytes.Length); }
        finally { Marshal.FreeCoTaskMem(buffer); }
    }
    static PropertyKey Key(uint id) { return new PropertyKey(Category, id); }
    static uint Unsigned(IPortableDeviceValues values, uint id)
    {
        PropertyKey key = Key(id);
        uint number;
        values.GetUnsignedIntegerValue(ref key, out number);
        return number;
    }
    static IPortableDeviceValues Values()
    {
        return (IPortableDeviceValues)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("0C15D503-D017-47CE-9016-7B3F978721CC")));
    }
    static IPortableDeviceValues Command(uint id)
    {
        IPortableDeviceValues values = Values();
        PropertyKey category = new PropertyKey(Common, 1001), command = new PropertyKey(Common, 1002);
        Guid guid = Category;
        values.SetGuidValue(ref category, ref guid);
        values.SetUnsignedIntegerValue(ref command, id);
        return values;
    }
    static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}

internal sealed class FujiPtpException : IOException
{
    public readonly uint Response;
    public FujiPtpException(uint response) : base("The camera rejected the settings request (PTP 0x" + response.ToString("X4") + ").") { Response = response; }
}
