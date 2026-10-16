using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using GearBackup;

// The MTP and PTP code against the fake Portable Devices API in FakePortableDevice.cs.
partial class Tests
{
    [Test("MTP device: listing folders and files")]
    static void TestMtpDeviceListing()
    {
        WithUi(() => {
            var modified = new DateTime(2026, 3, 4, 12, 30, 0, DateTimeKind.Local);
            var fake = new FakeMtpDevice();
            fake.Folder("S1", "Internal");
            fake.Folder("F1", "Samples", "S1");
            fake.File("A", "kick.wav", "KICK", "F1", modified);
            fake.File("B", "song.txt", "SONG", "S1");
            fake.Objects.Find(o => o.Id == "B").Created = modified.AddDays(1);
            var device = new MtpDevice(fake);
            List<MtpObject> all = device.ListAll();
            Assert(all.Count == 3 && all.Exists(o => o.RelativePath == "Samples" && o.IsFolder) && all.Exists(o => o.RelativePath == @"Samples\kick.wav" && o.Size == 4),
                "a single storage's contents are the root: " + string.Join(", ", all.ConvertAll(o => o.RelativePath)));
            Assert(Math.Abs((all.Find(o => o.Id == "A").Modified - modified).TotalSeconds) < 2, "the modified date comes through the device's date property");
            Assert(Math.Abs((all.Find(o => o.Id == "B").Modified - modified.AddDays(1)).TotalSeconds) < 2, "a camera that reports no modified date gets the created date");

            var two = new FakeMtpDevice();
            two.Folder("S1", "Internal"); two.Folder("S2", "Card");
            two.File("A", "a.txt", "A", "S1"); two.File("B", "b.txt", "B", "S2");
            List<MtpObject> both = new MtpDevice(two).ListAll();
            Assert(both.Exists(o => o.RelativePath == @"Internal\a.txt") && both.Exists(o => o.RelativePath == @"Card\b.txt"), "with several storages each is a top folder");

            var batched = new FakeMtpDevice();
            batched.Folder("S1", "Internal");
            for (int i = 0; i < 250; i++) batched.File("f" + i, "f" + i + ".txt", "x", "S1");
            Assert(new MtpDevice(batched).ListAll().Count == 250, "folders longer than one batch of 100 are read completely");

            var looping = new FakeMtpDevice { RepeatForever = true };
            looping.Folder("S1", "Internal");
            for (int i = 0; i < 3; i++) looping.File("f" + i, "f" + i + ".txt", "x", "S1");
            Assert(new MtpDevice(looping).ListAll().Count == 3, "a device that repeats its listing is stopped, each item kept once");

            var noSize = new FakeMtpDevice();
            noSize.Folder("S1", "Internal"); noSize.File("A", "a.txt", "AAAA", "S1").NoSize = true;
            Assert(new MtpDevice(noSize).ListAll()[0].Size == 0, "an object that reports no size stays at zero");
        });
    }

    [Test("MTP device: downloads and closing")]
    static void TestMtpDeviceDownload(string dir)
    {
        string root = Folder(dir, "mtp-download");
        WithUi(() => {
            var fake = new FakeMtpDevice();
            fake.Folder("S1", "Internal"); fake.File("A", "a.txt", "HELLO DEVICE", "S1");
            var device = new MtpDevice(fake);
            MtpObject item = device.ListAll()[0];
            long last = 0;
            string target = Path.Combine(root, "a.txt");
            device.Download(item, target, done => last = done);
            Assert(File.ReadAllText(target) == "HELLO DEVICE" && last == 12, "a file's content is copied with progress");

            var shorter = new MtpObject { Id = "A", Size = 20 };
            Throws(() => device.Download(shorter, Path.Combine(root, "short.txt"), null), "a download that ends before its declared size fails");
            var bigger = new MtpObject { Id = "A", Size = 5 };
            Throws(() => device.Download(bigger, Path.Combine(root, "big.txt"), null), "a download over its declared size fails");
            Throws(() => device.Download(new MtpObject { Id = "A", Size = -1 }, Path.Combine(root, "neg.txt"), null), "a negative size is refused");

            device.Dispose();
            Assert(fake.Closed == 1, "closing the device releases it");
            fake.CloseFails = new COMException("unplugged");
            new MtpDevice(fake).Dispose();
            Assert(fake.Closed == 2, "a device that is already unplugged does not make closing fail");
        });
    }

    [Test("Fuji PTP: reading properties")]
    static void TestFujiPtpReads()
    {
        WithUi(() => {
            var fake = new FakeFujiCamera();
            fake.Payload = new byte[40000];
            for (int i = 0; i < fake.Payload.Length; i++) fake.Payload[i] = (byte)(i * 7);
            using (var camera = new FujiPtp(fake))
            {
                byte[] got = camera.ReadProperty(0xD18C);
                Assert(got.Length == 40000 && got[39999] == fake.Payload[39999] && got[12345] == fake.Payload[12345], "a long answer is read chunk by chunk");
                Assert(fake.Operations[0] == 0x1015, "a property read asks for operation 0x1015");
                Assert(string.Join(",", fake.Commands) == "13,15,15,15,17", "start, three chunks and end: " + string.Join(",", fake.Commands));
                fake.Commands.Clear();
                camera.DeviceInfo();
                Assert(fake.Operations[1] == 0x1001, "device information is operation 0x1001");
            }

            var big = new FakeFujiCamera { DeclaredSize = 2 * 1024 * 1024 };
            using (var camera = new FujiPtp(big))
            {
                Throws(() => camera.ReadProperty(1), "an answer over the size limit is refused");
                Assert(big.Commands[big.Commands.Count - 1] == 17, "the read is ended even when it is refused");
            }

            var empty = new FakeFujiCamera { Payload = new byte[100], EmptyChunks = true };
            using (var camera = new FujiPtp(empty))
            {
                Throws(() => camera.ReadProperty(1), "a camera that hands over nothing is reported as incomplete");
                Assert(empty.Commands[empty.Commands.Count - 1] == 17, "an incomplete read is still ended");
            }

            var rejecting = new FakeFujiCamera { Payload = new byte[10], EndResponse = 0x2002 };
            using (var camera = new FujiPtp(rejecting))
            {
                try { camera.ReadProperty(1); Assert(false, "unreachable"); }
                catch (FujiPtpException ex) { Assert(ex.Response == 0x2002 && ex.Message.Contains("0x2002"), "a response other than OK is reported with its code"); }
            }

            var broken = new FakeFujiCamera { FailCommandHr = unchecked((int)0x80004005) };
            using (var camera = new FujiPtp(broken))
            {
                try { camera.DeviceInfo(); Assert(false, "unreachable"); }
                catch (COMException ex) { Assert(ex.ErrorCode == unchecked((int)0x80004005), "a failure code from the device is raised as it is"); }
            }
        });
    }

    [Test("Fuji PTP: selecting a recipe")]
    static void TestFujiPtpSelectRecipe()
    {
        WithUi(() => {
            var fake = new FakeFujiCamera();
            using (var camera = new FujiPtp(fake))
            {
                camera.SelectRecipe(3);
                Assert(string.Join(",", fake.Commands) == "14,16,17" && fake.Operations[0] == 0x1016, "selecting a recipe is start, write and end: " + string.Join(",", fake.Commands));
                Assert(fake.Written.Count == 1 && fake.Written[0].Length == 2 && fake.Written[0][0] == 3 && fake.Written[0][1] == 0, "the slot is written as a 16-bit value");
                fake.Commands.Clear();
                Throws(() => camera.SelectRecipe(8), "a recipe slot above 7 is refused");
                Assert(fake.Commands.Count == 0, "an invalid slot sends nothing to the camera");
            }
        });
    }

    [Test("USB detection from the disk list")]
    static void TestUsbDetection()
    {
        var disks = UsbDevices.ListDisks; var drives = UsbDevices.ListDrives;
        try
        {
            Func<string, string, string, UsbDevices.UsbDisk> disk = (pnp, model, drive) =>
                new UsbDevices.UsbDisk { PnpId = pnp, Model = model, FindDrive = () => drive };
            UsbDevices.ListDrives = () => new List<UsbDevices.RemovableDrive>();
            DeviceProfile found; string pnpId;
            UsbDevices.ListDisks = () => new List<UsbDevices.UsbDisk> {
                disk(@"USBSTOR\DISK&VEN_SANDISK&PROD_CRUZER", "SANDISK CRUZER", @"E:\"),
                disk(@"USBSTOR\DISK&VEN_M8&PROD_X", "M8 DISK", @"F:\") };
            Assert(UsbDevices.FindDevice(out found, out pnpId) == @"F:\" && found == Devices.M8 && pnpId == @"USBSTOR\DISK&VEN_M8&PROD_X",
                "the M8 disk is found among other USB disks, with its device instance ID for ejecting");
            Assert(UsbDevices.FindDevice(out found, out pnpId, Devices.TrackerMini) == null && found == null && pnpId == null, "a search for another device type ignores the M8");
            UsbDevices.ListDisks = () => new List<UsbDevices.UsbDisk> { disk(@"USBSTOR\DISK&VEN_M8&PROD_X", "M8 DISK", null) };
            Assert(UsbDevices.FindDevice(out found, out pnpId) == null, "a matching disk without a drive letter is not a source");
            UsbDevices.ListDisks = () => new List<UsbDevices.UsbDisk>();
            UsbDevices.ListDrives = () => new List<UsbDevices.RemovableDrive> { new UsbDevices.RemovableDrive { Label = "usb stick", Root = @"G:\" }, new UsbDevices.RemovableDrive { Label = "m8", Root = @"H:\" } };
            Assert(UsbDevices.FindDevice(out found, out pnpId) == @"H:\" && found == Devices.M8 && pnpId == null, "a removable drive labelled M8 is found, but cannot be ejected");
            UsbDevices.ListDrives = () => new List<UsbDevices.RemovableDrive> { new UsbDevices.RemovableDrive { Label = "photos", Root = @"G:\" } };
            Assert(UsbDevices.FindDevice(out found, out pnpId) == null, "nothing matching gives no source");
            WithUi(() => Assert(UsbDevices.EjectDrive(null).Contains("label only"), "a drive found by label cannot be ejected automatically"));
        }
        finally { UsbDevices.ListDisks = disks; UsbDevices.ListDrives = drives; }
    }
}
