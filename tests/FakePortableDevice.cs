using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using CT = System.Runtime.InteropServices.ComTypes;
using GearBackup;

// Stand-ins for Windows' Portable Devices API, so the MTP and PTP code can be tested without a device. The values
// objects the code builds and reads are Windows' own (they work without a device); only the device, its folders,
// properties and file streams are faked here.
partial class Tests
{
    static readonly Guid ValuesClass = new Guid("0C15D503-D017-47CE-9016-7B3F978721CC");
    static IPortableDeviceValues NewValues()
    {
        return (IPortableDeviceValues)Activator.CreateInstance(Type.GetTypeFromCLSID(ValuesClass));
    }

    // A device that answers nothing by itself; subclasses answer commands and provide content.
    class FakePortableDevice : IPortableDevice
    {
        public int Closed;
        public Exception CloseFails;
        public IPortableDeviceContent ContentObject;
        public void Open(string pnpDeviceId, object clientInfo) { }
        public virtual void SendCommand(uint flags, IntPtr parameters, out IntPtr results) { throw new NotImplementedException(); }
        public void Content(out IPortableDeviceContent content) { content = ContentObject; }
        public void Capabilities(out IntPtr capabilities) { throw new NotImplementedException(); }
        public void Cancel() { }
        public void Close() { Closed++; if (CloseFails != null) throw CloseFails; }
        public void Advise(uint flags, IntPtr callback, IntPtr parameters, out IntPtr cookie) { throw new NotImplementedException(); }
        public void Unadvise(string cookie) { }
        public void GetPnPDeviceID(out IntPtr pnpDeviceId) { throw new NotImplementedException(); }
    }

    // Answers the Fuji settings commands the way the camera does: a read starts with a context and a size, hands the
    // payload over in chunks, and ends with a response code.
    sealed class FakeFujiCamera : FakePortableDevice
    {
        static readonly Guid Category = new Guid("4D545058-1A2E-4106-A357-771E0819FC56");
        static readonly Guid Common = new Guid("F0422A9C-5DC8-4440-B5BD-5DF28835658A");
        public byte[] Payload = new byte[0];
        public ulong DeclaredSize = ulong.MaxValue;     // what the start answer says; defaults to Payload.Length
        public uint EndResponse = 0x2001;
        public int FailCommandHr;                       // an HRESULT to return for every command when negative
        public uint ChunkLimit = uint.MaxValue;         // the most bytes the camera hands over per read
        public bool EmptyChunks;
        public readonly List<uint> Commands = new List<uint>(), Operations = new List<uint>();
        public readonly List<byte[]> Written = new List<byte[]>();
        int position;

        static PropertyKey Cat(uint id) { return new PropertyKey(Category, id); }
        static PropertyKey Com(uint id) { return new PropertyKey(Common, id); }

        public override void SendCommand(uint flags, IntPtr parameters, out IntPtr results)
        {
            var request = (IPortableDeviceValues)Marshal.GetObjectForIUnknown(parameters);
            uint command; PropertyKey commandKey = Com(1002);
            request.GetUnsignedIntegerValue(ref commandKey, out command);
            Commands.Add(command);
            IPortableDeviceValues answer = NewValues();
            PropertyKey errorKey = Com(1003);
            answer.SetErrorValue(ref errorKey, FailCommandHr < 0 ? FailCommandHr : 0);
            PropertyKey context = Cat(1006), size = Cat(1007), number = Cat(1008), actual = Cat(1009), written = Cat(1011), data = Cat(1012), code = Cat(1001), response = Cat(1003);
            switch (command)
            {
                case 13: case 14:
                    uint operation; request.GetUnsignedIntegerValue(ref code, out operation); Operations.Add(operation);
                    position = 0;
                    answer.SetStringValue(ref context, "context-1");
                    answer.SetUnsignedLargeIntegerValue(ref size, DeclaredSize == ulong.MaxValue ? (ulong)Payload.Length : DeclaredSize);
                    break;
                case 15:
                    uint wanted; request.GetUnsignedIntegerValue(ref number, out wanted);
                    uint count = EmptyChunks ? 0 : (uint)Math.Min(Math.Min(wanted, ChunkLimit), Payload.Length - position);
                    IntPtr buffer = Marshal.AllocCoTaskMem((int)Math.Max(wanted, 1));
                    try
                    {
                        if (count > 0) Marshal.Copy(Payload, position, buffer, (int)count);
                        answer.SetBufferValue(ref data, buffer, wanted);
                    }
                    finally { Marshal.FreeCoTaskMem(buffer); }
                    position += (int)count;
                    answer.SetUnsignedIntegerValue(ref actual, count);
                    break;
                case 16:
                    IntPtr bytes; uint length; request.GetBufferValue(ref data, out bytes, out length);
                    var copy = new byte[length]; Marshal.Copy(bytes, copy, 0, copy.Length); Marshal.FreeCoTaskMem(bytes);
                    Written.Add(copy);
                    answer.SetUnsignedIntegerValue(ref written, length);
                    break;
                case 17:
                    answer.SetUnsignedIntegerValue(ref response, EndResponse);
                    break;
            }
            results = Marshal.GetIUnknownForObject(answer);
        }
    }

    // One object on a fake MTP device.
    sealed class FakeObject
    {
        public string Id, Name, Parent = "DEVICE";
        public bool IsFolder;
        public byte[] Content = new byte[0];
        public DateTime? Modified, Created;
        public bool NoSize;
    }

    sealed class FakeMtpDevice : FakePortableDevice
    {
        public readonly List<FakeObject> Objects = new List<FakeObject>();
        public bool RepeatForever;       // a faulty device that lists the same ids again and again
        public FakeMtpDevice() { ContentObject = new FakeContent(this); }
        public FakeObject Folder(string id, string name, string parent = "DEVICE") { var o = new FakeObject { Id = id, Name = name, Parent = parent, IsFolder = true }; Objects.Add(o); return o; }
        public FakeObject File(string id, string name, string text, string parent, DateTime? modified = null)
        {
            var o = new FakeObject { Id = id, Name = name, Parent = parent, Content = System.Text.Encoding.UTF8.GetBytes(text), Modified = modified };
            Objects.Add(o); return o;
        }
    }

    sealed class FakeContent : IPortableDeviceContent
    {
        readonly FakeMtpDevice device;
        public FakeContent(FakeMtpDevice device) { this.device = device; }
        public void EnumObjects(uint flags, string parentObjectId, IntPtr filter, out IEnumPortableDeviceObjectIDs objects)
        {
            var ids = new List<string>();
            foreach (var o in device.Objects) if (o.Parent == parentObjectId) ids.Add(o.Id);
            objects = new FakeIdList(ids, device.RepeatForever);
        }
        public void Properties(out IPortableDeviceProperties properties) { properties = new FakeProperties(device); }
        public void Transfer(out IPortableDeviceResources resources) { resources = new FakeResources(device); }
        public void CreateObjectWithPropertiesOnly(IntPtr values, out IntPtr objectId) { throw new NotImplementedException(); }
        public void CreateObjectWithPropertiesAndData(IntPtr values, out IntPtr data, ref uint optimalWriteBufferSize, out IntPtr cookie) { throw new NotImplementedException(); }
        public void Delete(uint options, IntPtr objectIds, out IntPtr results) { throw new NotImplementedException(); }
        public void GetObjectIDsFromPersistentUniqueIDs(IntPtr persistentUniqueIds, out IntPtr objectIds) { throw new NotImplementedException(); }
        public void Cancel() { }
        public void Move(IntPtr objectIds, string destinationFolderId, out IntPtr results) { throw new NotImplementedException(); }
        public void Copy(IntPtr objectIds, string destinationFolderId, out IntPtr results) { throw new NotImplementedException(); }
    }

    sealed class FakeIdList : IEnumPortableDeviceObjectIDs
    {
        readonly List<string> ids; readonly bool repeat; int next;
        public FakeIdList(List<string> ids, bool repeat) { this.ids = ids; this.repeat = repeat; }
        // Like Windows: S_OK when the whole batch was filled, S_FALSE when the list ran out first.
        public int Next(uint count, IntPtr[] objectIds, out uint fetched)
        {
            int give = (int)Math.Min(count, repeat ? count : (uint)(ids.Count - next));
            for (int i = 0; i < give && ids.Count > 0; i++) objectIds[i] = Marshal.StringToCoTaskMemUni(ids[(next + i) % ids.Count]);
            if (ids.Count == 0) give = 0;
            next += give; fetched = (uint)give;
            return give == (int)count ? 0 : 1;
        }
        public void Skip(uint count) { next += (int)count; }
        public void Reset() { next = 0; }
        public void Clone(out IEnumPortableDeviceObjectIDs clone) { throw new NotImplementedException(); }
        public void Cancel() { }
    }

    sealed class FakeProperties : IPortableDeviceProperties
    {
        static readonly Guid ObjectProperties = new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C");
        static readonly Guid FolderType = new Guid("27E2E392-A111-48E0-AB0C-E17705A05F85");
        readonly FakeMtpDevice device;
        public FakeProperties(FakeMtpDevice device) { this.device = device; }
        static PropertyKey Key(uint id) { return new PropertyKey(ObjectProperties, id); }

        public void GetValues(string objectId, IntPtr keys, out IPortableDeviceValues values)
        {
            FakeObject item = objectId == "DEVICE" ? null : device.Objects.Find(o => o.Id == objectId);
            IPortableDeviceValues result = NewValues();
            PropertyKey name = Key(12), type = Key(7), size = Key(11);
            result.SetStringValue(ref name, item == null ? objectId : item.Name);
            if (item != null && item.IsFolder) { Guid folder = FolderType; result.SetGuidValue(ref type, ref folder); }
            if (item != null && !item.IsFolder && !item.NoSize) result.SetUnsignedLargeIntegerValue(ref size, (ulong)item.Content.Length);
            if (item != null && item.Modified.HasValue) SetDate(result, Key(19), item.Modified.Value);
            if (item != null && item.Created.HasValue) SetDate(result, Key(18), item.Created.Value);
            values = result;
        }

        // Dates travel as a PROPVARIANT holding an OLE date (VT_DATE, 7).
        static void SetDate(IPortableDeviceValues values, PropertyKey key, DateTime local)
        {
            IntPtr variant = Marshal.AllocCoTaskMem(24);
            try
            {
                for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
                Marshal.WriteInt16(variant, 7);
                Marshal.WriteInt64(variant, 8, BitConverter.DoubleToInt64Bits(local.ToOADate()));
                values.SetValue(ref key, variant);
            }
            finally { Marshal.FreeCoTaskMem(variant); }
        }

        public void GetSupportedProperties(string objectId, out IntPtr keys) { throw new NotImplementedException(); }
        public void GetPropertyAttributes(string objectId, ref PropertyKey key, out IntPtr attributes) { throw new NotImplementedException(); }
        public void SetValues(string objectId, IntPtr values, out IntPtr results) { throw new NotImplementedException(); }
        public void Delete(string objectId, IntPtr keys) { throw new NotImplementedException(); }
        public void Cancel() { }
    }

    sealed class FakeResources : IPortableDeviceResources
    {
        readonly FakeMtpDevice device;
        public FakeResources(FakeMtpDevice device) { this.device = device; }
        public void GetStream(string objectId, ref PropertyKey key, uint mode, ref uint optimalBufferSize, out CT.IStream stream)
        {
            FakeObject item = device.Objects.Find(o => o.Id == objectId);
            if (item == null) throw new COMException("No such object", unchecked((int)0x80070490));
            optimalBufferSize = 0;
            stream = new FakeStream(item.Content);
        }
        public void GetSupportedResources(string objectId, out IntPtr keys) { throw new NotImplementedException(); }
        public void GetResourceAttributes(string objectId, ref PropertyKey key, out IntPtr attributes) { throw new NotImplementedException(); }
        public void Delete(string objectId, IntPtr keys) { throw new NotImplementedException(); }
        public void Cancel() { }
        public void CreateResource(IntPtr resourceAttributes, out IntPtr data, ref uint optimalWriteBufferSize, out IntPtr cookie) { throw new NotImplementedException(); }
    }

    sealed class FakeStream : CT.IStream
    {
        readonly byte[] bytes; int position;
        public FakeStream(byte[] bytes) { this.bytes = bytes; }
        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int count = Math.Min(cb, bytes.Length - position);
            Array.Copy(bytes, position, pv, 0, count); position += count;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, count);
        }
        public void Write(byte[] pv, int cb, IntPtr pcbWritten) { throw new NotImplementedException(); }
        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition) { throw new NotImplementedException(); }
        public void SetSize(long libNewSize) { throw new NotImplementedException(); }
        public void CopyTo(CT.IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) { throw new NotImplementedException(); }
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) { }
        public void UnlockRegion(long libOffset, long cb, int dwLockType) { }
        public void Stat(out CT.STATSTG pstatstg, int grfStatFlag) { pstatstg = new CT.STATSTG { cbSize = bytes.Length }; }
        public void Clone(out CT.IStream ppstm) { throw new NotImplementedException(); }
    }
}
