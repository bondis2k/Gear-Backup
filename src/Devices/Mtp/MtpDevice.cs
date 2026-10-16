// Reading from a device that connects with MTP (Media Transfer Protocol) instead of as a drive, such as
// the OP-1 Field. Windows has no drive letter for these, so they're read through its Portable Devices
// API. Only reading is implemented: nothing here can change anything on the device.
//
// The COM declarations below must match Windows' definitions exactly, method for method; they were
// checked against the type libraries in PortableDeviceApi.dll and PortableDeviceTypes.dll.
using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using CT = System.Runtime.InteropServices.ComTypes;
using static GearBackup.MtpMirror;
using static GearBackup.PortableDevices;

namespace GearBackup
{
    static class PortableDevices
    {
        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(IntPtr value);

        // An OLE date from a device; DateTime.MinValue ("no date") for a value that isn't a valid date,
        // so one odd file doesn't stop the whole listing.
        internal static DateTime OleDate(double value)
        {
            if (double.IsNaN(value) || value < -657435.0 || value > 2958465.99999999) return DateTime.MinValue;
            try { return DateTime.FromOADate(value); }
            catch (ArgumentException) { return DateTime.MinValue; }
        }

        // Connected portable (MTP) devices whose USB instance ID contains usbVendorId, e.g. "VID_2367".
        internal static List<MtpDeviceInfo> FindMtpDevices(string usbVendorId)
        {
            var found = new List<MtpDeviceInfo>();
            using (var search = new ManagementObjectSearcher("SELECT DeviceID, Name, PNPClass FROM Win32_PnPEntity WHERE PNPClass = 'WPD'"))
                foreach (ManagementObject entity in search.Get())
                {
                    string id = (entity["DeviceID"] ?? "").ToString();
                    if (id.ToUpperInvariant().Contains(usbVendorId.ToUpperInvariant()))
                        found.Add(new MtpDeviceInfo { InstanceId = id, FriendlyName = (entity["Name"] ?? "").ToString() });
                }
            return found;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid Format;
        public uint Id;
        public PropertyKey(Guid format, uint id) { Format = format; Id = id; }
    }

    [ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDevice
    {
        void Open([MarshalAs(UnmanagedType.LPWStr)] string pnpDeviceId, [MarshalAs(UnmanagedType.IUnknown)] object clientInfo);
        void SendCommand(uint flags, IntPtr parameters, out IntPtr results);
        void Content(out IPortableDeviceContent content);
        void Capabilities(out IntPtr capabilities);
        void Cancel();
        void Close();
        void Advise(uint flags, IntPtr callback, IntPtr parameters, out IntPtr cookie);
        void Unadvise([MarshalAs(UnmanagedType.LPWStr)] string cookie);
        void GetPnPDeviceID(out IntPtr pnpDeviceId);
    }

    [ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceContent
    {
        void EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentObjectId, IntPtr filter, out IEnumPortableDeviceObjectIDs objects);
        void Properties(out IPortableDeviceProperties properties);
        void Transfer(out IPortableDeviceResources resources);
        // The remaining methods (create, delete, move, copy) change the device and are never called.
        void CreateObjectWithPropertiesOnly(IntPtr values, out IntPtr objectId);
        void CreateObjectWithPropertiesAndData(IntPtr values, out IntPtr data, ref uint optimalWriteBufferSize, out IntPtr cookie);
        void Delete(uint options, IntPtr objectIds, out IntPtr results);
        void GetObjectIDsFromPersistentUniqueIDs(IntPtr persistentUniqueIds, out IntPtr objectIds);
        void Cancel();
        void Move(IntPtr objectIds, [MarshalAs(UnmanagedType.LPWStr)] string destinationFolderId, out IntPtr results);
        void Copy(IntPtr objectIds, [MarshalAs(UnmanagedType.LPWStr)] string destinationFolderId, out IntPtr results);
    }

    [ComImport, Guid("10ece955-cf41-4728-bfa0-41eedf1bbf19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumPortableDeviceObjectIDs
    {
        [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] objectIds, out uint fetched);
        void Skip(uint count);
        void Reset();
        void Clone(out IEnumPortableDeviceObjectIDs clone);
        void Cancel();
    }

    [ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceProperties
    {
        void GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IntPtr keys);
        void GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IntPtr attributes);
        void GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IntPtr keys, out IPortableDeviceValues values);
        void SetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IntPtr values, out IntPtr results);
        void Delete([MarshalAs(UnmanagedType.LPWStr)] string objectId, IntPtr keys);
        void Cancel();
    }

    [ComImport, Guid("fd8878ac-d841-4d17-891c-e6829cdb6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceResources
    {
        void GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IntPtr keys);
        void GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IntPtr attributes);
        void GetStream([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, uint mode, ref uint optimalBufferSize, out CT.IStream stream);
        void Delete([MarshalAs(UnmanagedType.LPWStr)] string objectId, IntPtr keys);
        void Cancel();
        void CreateResource(IntPtr resourceAttributes, out IntPtr data, ref uint optimalWriteBufferSize, out IntPtr cookie);
    }

    [ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceValues
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref PropertyKey key, IntPtr value);
        void SetValue(ref PropertyKey key, IntPtr value);
        void GetValue(ref PropertyKey key, IntPtr value);
        void SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
        void SetUnsignedIntegerValue(ref PropertyKey key, uint value);
        void GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
        void SetSignedIntegerValue(ref PropertyKey key, int value);
        void GetSignedIntegerValue(ref PropertyKey key, out int value);
        void SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
        void GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
        void SetSignedLargeIntegerValue(ref PropertyKey key, long value);
        void GetSignedLargeIntegerValue(ref PropertyKey key, out long value);
        void SetFloatValue(ref PropertyKey key, float value);
        void GetFloatValue(ref PropertyKey key, out float value);
        void SetErrorValue(ref PropertyKey key, int value);
        void GetErrorValue(ref PropertyKey key, out int value);
        void SetKeyValue(ref PropertyKey key, ref PropertyKey value);
        void GetKeyValue(ref PropertyKey key, out PropertyKey value);
        void SetBoolValue(ref PropertyKey key, int value);
        void GetBoolValue(ref PropertyKey key, out int value);
        void SetIUnknownValue(ref PropertyKey key, IntPtr value);
        void GetIUnknownValue(ref PropertyKey key, out IntPtr value);
        void SetGuidValue(ref PropertyKey key, ref Guid value);
        void GetGuidValue(ref PropertyKey key, out Guid value);
        // The remaining methods aren't used; they're declared so the method order matches Windows'.
        void SetBufferValue(ref PropertyKey key, IntPtr value, uint size);
        void GetBufferValue(ref PropertyKey key, out IntPtr value, out uint size);
        void SetIPortableDeviceValuesValue(ref PropertyKey key, IntPtr value);
        void GetIPortableDeviceValuesValue(ref PropertyKey key, out IntPtr value);
        void SetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, IntPtr value);
        void GetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, out IntPtr value);
        void SetIPortableDeviceKeyCollectionValue(ref PropertyKey key, IntPtr value);
        void GetIPortableDeviceKeyCollectionValue(ref PropertyKey key, out IntPtr value);
        void SetIPortableDeviceValuesCollectionValue(ref PropertyKey key, IntPtr value);
        void GetIPortableDeviceValuesCollectionValue(ref PropertyKey key, out IntPtr value);
        void RemoveValue(ref PropertyKey key);
        void CopyValuesFromPropertyStore(IntPtr store);
        void CopyValuesToPropertyStore(IntPtr store);
        void Clear();
    }

    // One file or folder on an MTP device.
    internal sealed class MtpObject
    {
        public string Id, Name, RelativePath;
        public bool IsFolder;
        public long Size;
        public DateTime Modified;   // local time; DateTime.MinValue when the device doesn't say
    }

    // A connected MTP device, found by its USB vendor ID.
    internal sealed class MtpDeviceInfo
    {
        public string InstanceId;    // e.g. USB\VID_2367&PID_0102\L1PRC121
        public string FriendlyName;  // e.g. OP-1
        public string Serial { get { int i = InstanceId.LastIndexOf('\\'); return i < 0 ? InstanceId : InstanceId.Substring(i + 1); } }
        // The device interface path Windows' Portable Devices API opens.
        public string InterfacePath
        {
            get { return @"\\?\" + InstanceId.Replace('\\', '#').ToLowerInvariant() + "#{6ac27878-a6fa-4155-ba85-f98f491d4f33}"; }
        }
    }

    // An open connection to an MTP device, for reading only.
    internal sealed class MtpDevice : IMtpSource, IDisposable
    {
        static readonly Guid ObjectProperties = new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C");
        static PropertyKey Key(uint id) { return new PropertyKey(ObjectProperties, id); }
        static readonly PropertyKey NameKey = Key(4), ContentTypeKey = Key(7), SizeKey = Key(11),
            OriginalFileNameKey = Key(12), DateCreatedKey = Key(18), DateModifiedKey = Key(19), DateAuthoredKey = Key(20);
        static readonly PropertyKey DefaultResource = new PropertyKey(new Guid("E81E79BE-34F0-41BF-B53F-F1A06AE87842"), 0);
        static readonly Guid FolderType = new Guid("27E2E392-A111-48E0-AB0C-E17705A05F85");
        static readonly Guid FunctionalObjectType = new Guid("99ED0160-17FF-4C44-9D98-1D7A6F941921");
        const string DeviceObjectId = "DEVICE";
        const uint StgmRead = 0;

        readonly IPortableDevice device;
        readonly IPortableDeviceContent content;
        readonly IPortableDeviceProperties properties;
        readonly IPortableDeviceResources resources;

        public MtpDevice(string interfacePath)
        {
            device = (IPortableDevice)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("F7C0039A-4762-488A-B4B3-760EF9A1BA9B")));
            object clientInfo = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("0C15D503-D017-47CE-9016-7B3F978721CC")));
            device.Open(interfacePath, clientInfo);
            device.Content(out content);
            content.Properties(out properties);
            content.Transfer(out resources);
        }

        public void Dispose()
        {
            try { device.Close(); } catch (COMException) { }
        }

        // Every file and folder below the device's storage, with paths relative to it. With a single
        // storage (as on the OP-1 Field), its contents are the root; with several, each is a top folder.
        public List<MtpObject> ListAll()
        {
            var all = new List<MtpObject>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { DeviceObjectId };
            List<string> storages = Children(DeviceObjectId);
            foreach (string storage in storages)
            {
                if (!seen.Add(storage)) continue;
                string prefix = storages.Count == 1 ? "" : Read(storage, "").Name;
                Walk(storage, prefix, all, seen, 0);
            }
            return all;
        }

        // Deeper than any real device's folders; also stops a device whose listing never ends.
        const int MaxDepth = 64;
        // Far more than any real device holds (an X100VI card with tens of thousands of photos is well
        // below); a faulty device listing without end is stopped instead of filling memory.
        internal const int MaxFolderItems = 200000, MaxItems = 2000000;

        // `seen` holds every object already listed, and a repeat is skipped: a faulty device that lists a
        // folder inside itself would otherwise be walked until the program runs out of stack.
        void Walk(string parentId, string parentPath, List<MtpObject> into, HashSet<string> seen, int depth)
        {
            if (depth > MaxDepth) throw new IOException("The device's folders are nested too deeply to read.");
            foreach (string id in Children(parentId))
            {
                OperationContext.Current.CheckCancellation();
                if (!seen.Add(id)) continue;
                if (into.Count >= MaxItems) throw new IOException("The device lists more than " + MaxItems.ToString("N0") + " items; reconnect it and try again.");
                MtpObject item = Read(id, parentPath);
                into.Add(item);
                OperationContext.Current.Progress(Stage.Scanning, item.RelativePath, 0, 0, into.Count);
                if (item.IsFolder) Walk(id, item.RelativePath, into, seen, depth + 1);
            }
        }

        // A folder's object IDs, fetched 100 at a time: one at a time made listing an OP-XY with 467 items
        // about 30% slower (0.33 s against 0.22 s).
        List<string> Children(string parentId)
        {
            IEnumPortableDeviceObjectIDs objects;
            content.EnumObjects(0, parentId, IntPtr.Zero, out objects);
            var batch = new IntPtr[100];
            return CollectIds(into => {
                uint fetched;
                int hr = objects.Next((uint)batch.Length, batch, out fetched);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                for (int i = 0; i < fetched && i < batch.Length; i++)
                {
                    into.Add(Marshal.PtrToStringUni(batch[i]));
                    Marshal.FreeCoTaskMem(batch[i]);
                }
                // S_FALSE (1), or fewer IDs than asked for, means the list is finished.
                return hr == 0 && fetched >= batch.Length;
            });
        }

        // Adds the next batch of IDs to the list; returns whether more may follow.
        internal delegate bool NextIds(List<string> into);

        // Collects a folder's IDs batch by batch from `next`. Between batches it checks for Cancel; it stops
        // when a full batch brings no new IDs (a faulty device repeating itself), and refuses a folder with
        // more than `limit` items, so a device that never stops listing can't loop forever or fill memory.
        internal static List<string> CollectIds(NextIds next, int limit = MaxFolderItems)
        {
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var batch = new List<string>();
            while (true)
            {
                OperationContext.Current.CheckCancellation();
                batch.Clear();
                bool more = next(batch);
                int added = 0;
                foreach (string id in batch)
                    if (seen.Add(id)) { ids.Add(id); added++; }
                if (ids.Count > limit) throw new IOException("A folder on the device lists more than " + limit.ToString("N0") + " items; reconnect it and try again.");
                if (!more || added == 0) return ids;
            }
        }

        MtpObject Read(string id, string parentPath)
        {
            IPortableDeviceValues values;
            properties.GetValues(id, IntPtr.Zero, out values);
            var item = new MtpObject { Id = id, Modified = DateTime.MinValue };
            item.Name = StringValue(values, OriginalFileNameKey) ?? StringValue(values, NameKey) ?? id;
            // `ref` needs a variable, and the keys are readonly fields.
            PropertyKey typeKey = ContentTypeKey, sizeKey = SizeKey;
            Guid type;
            try { values.GetGuidValue(ref typeKey, out type); } catch (COMException) { type = Guid.Empty; }
            item.IsFolder = type == FolderType || type == FunctionalObjectType;
            ulong size;
            try { values.GetUnsignedLargeIntegerValue(ref sizeKey, out size); item.Size = (long)size; } catch (COMException) { }
            // The modified date, or for a camera (which reports none) the date the photo was taken.
            item.Modified = DateValue(values, DateModifiedKey);
            if (item.Modified == DateTime.MinValue) item.Modified = DateValue(values, DateCreatedKey);
            if (item.Modified == DateTime.MinValue) item.Modified = DateValue(values, DateAuthoredKey);
            item.RelativePath = parentPath.Length == 0 ? item.Name : parentPath + "\\" + item.Name;
            Marshal.ReleaseComObject(values);
            return item;
        }

        static string StringValue(IPortableDeviceValues values, PropertyKey key)
        {
            try { string value; values.GetStringValue(ref key, out value); return string.IsNullOrEmpty(value) ? null : value; }
            catch (COMException) { return null; }
        }

        // Dates come as a PROPVARIANT holding an OLE date (VT_DATE, 7).
        static DateTime DateValue(IPortableDeviceValues values, PropertyKey key)
        {
            IntPtr variant = Marshal.AllocCoTaskMem(32);
            try
            {
                for (int i = 0; i < 32; i++) Marshal.WriteByte(variant, i, 0);
                try { values.GetValue(ref key, variant); } catch (COMException) { return DateTime.MinValue; }
                DateTime result = Marshal.ReadInt16(variant) == 7
                    ? OleDate(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(variant, 8)))
                    : DateTime.MinValue;
                PropVariantClear(variant);
                return result;
            }
            finally { Marshal.FreeCoTaskMem(variant); }
        }

        // The device's reading of the buffer size it works best with, kept between 256 KB and 4 MB.
        internal static int DownloadBufferSize(uint optimal)
        {
            return (int)Math.Min(Math.Max(optimal, 256u * 1024), 4u * 1024 * 1024);
        }

        // Copies a file's content from the device into `to` (which must not exist yet).
        public void Download(MtpObject item, string to, Action<long> progress)
        {
            uint bufferSize = 0;
            CT.IStream stream;
            PropertyKey key = DefaultResource;
            resources.GetStream(item.Id, ref key, StgmRead, ref bufferSize, out stream);
            try
            {
                var buffer = new byte[DownloadBufferSize(bufferSize)];
                IntPtr readCount = Marshal.AllocCoTaskMem(4);
                try
                {
                    using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
                    {
                        long done = 0;
                        while (true)
                        {
                            OperationContext.Current.CheckCancellation();
                            stream.Read(buffer, buffer.Length, readCount);
                            int read = Marshal.ReadInt32(readCount);
                            if (read <= 0) break;
                            output.Write(buffer, 0, read);
                            done += read;
                            if (progress != null) progress(done);
                        }
                    }
                }
                finally { Marshal.FreeCoTaskMem(readCount); }
            }
            finally { Marshal.ReleaseComObject(stream); }
        }
    }

}
