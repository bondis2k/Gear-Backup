using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GearBackup
{
    // Small, scoped adapter for Windows' SQLite engine. No SQL from backup files is executed by callers;
    // values are bound parameters, extensions stay disabled, and published catalogs are opened read-only.
    internal sealed class SqliteDatabase : IDisposable
    {
        IntPtr handle;
        readonly Native.Progress progress;
        int steps;

        internal SqliteDatabase(string path, bool readOnly)
        {
            int code = Native.sqlite3_open_v2(Utf8(path), out handle, readOnly ? 1 : 6, IntPtr.Zero);
            try
            {
                Check(code);
                Native.sqlite3_limit(handle, 0, 1024 * 1024); // Value/string length.
                Native.sqlite3_limit(handle, 1, 16384); // SQL text length.
                Native.sqlite3_limit(handle, 2, 64); // Columns.
                Native.sqlite3_limit(handle, 4, 8); // Compound SELECT terms.
                int unused;
                Native.sqlite3_db_config(handle, 1010, 1, out unused); // Defensive (older engines may ignore).
                Native.sqlite3_db_config(handle, 1017, 0, out unused); // Untrusted schema.
                progress = context => {
                    var active = OperationContext.Active;
                    return ++steps > 20000 || (active != null && active.Cancellation.IsCancellationRequested) ? 1 : 0;
                };
                Native.sqlite3_progress_handler(handle, 1000, progress, IntPtr.Zero);
                Execute("PRAGMA trusted_schema=OFF");
                if (readOnly) Execute("PRAGMA query_only=ON");
            }
            catch { Dispose(); throw; }
        }

        internal Statement Prepare(string sql) { steps = 0; return new Statement(this, sql); }
        internal void Execute(string sql, params object[] values)
        {
            using (var statement = Prepare(sql)) { statement.Bind(values); while (statement.Read()) { } }
        }
        void Check(int code)
        {
            if (code == 0) return;
            if (code == 9 && OperationContext.Active != null) OperationContext.Active.CheckCancellation();
            throw new IOException("SQLite catalog error " + code + ": " + (handle == IntPtr.Zero ? "Cannot open database." : Text(Native.sqlite3_errmsg(handle))));
        }
        static byte[] Utf8(string text) { return Encoding.UTF8.GetBytes(text + "\0"); }
        static string Text(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return null;
            int length = 0;
            while (length <= 1024 * 1024 && Marshal.ReadByte(pointer, length) != 0) length++;
            if (length > 1024 * 1024) throw new IOException("SQLite catalog value is too large.");
            byte[] bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            Native.sqlite3_progress_handler(handle, 0, null, IntPtr.Zero);
            Native.sqlite3_close_v2(handle); handle = IntPtr.Zero;
            GC.KeepAlive(progress);
        }

        internal sealed class Statement : IDisposable
        {
            readonly SqliteDatabase database;
            IntPtr handle;
            internal Statement(SqliteDatabase database, string sql)
            {
                this.database = database;
                IntPtr tail;
                int code = Native.sqlite3_prepare_v2(database.handle, Utf8(sql), -1, out handle, out tail);
                if (code != 0) { Dispose(); database.Check(code); }
            }
            internal void Bind(params object[] values)
            {
                database.steps = 0;
                database.Check(Native.sqlite3_reset(handle));
                database.Check(Native.sqlite3_clear_bindings(handle));
                for (int i = 0; i < values.Length; i++)
                {
                    object value = values[i];
                    int code = value == null ? Native.sqlite3_bind_null(handle, i + 1)
                        : value is string ? Native.sqlite3_bind_text(handle, i + 1, Utf8((string)value), -1, new IntPtr(-1))
                        : Native.sqlite3_bind_int64(handle, i + 1, Convert.ToInt64(value));
                    database.Check(code);
                }
            }
            internal bool Read()
            {
                int code = Native.sqlite3_step(handle);
                if (code == 100) return true;
                if (code == 101) return false;
                database.Check(code); return false;
            }
            internal string String(int column) { return Text(Native.sqlite3_column_text(handle, column)); }
            internal long Number(int column) { return Native.sqlite3_column_int64(handle, column); }
            public void Dispose() { if (handle != IntPtr.Zero) { Native.sqlite3_finalize(handle); handle = IntPtr.Zero; } }
        }

        static class Native
        {
            const string Library = "winsqlite3.dll";
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Progress(IntPtr context);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_close_v2(IntPtr db);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_limit(IntPtr db, int category, int value);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_db_config(IntPtr db, int option, int value, out int result);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern void sqlite3_progress_handler(IntPtr db, int interval, Progress callback, IntPtr context);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, out IntPtr tail);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_step(IntPtr statement);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_reset(IntPtr statement);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_clear_bindings(IntPtr statement);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_finalize(IntPtr statement);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_bind_null(IntPtr statement, int index);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)] internal static extern long sqlite3_column_int64(IntPtr statement, int column);
        }
    }

}
