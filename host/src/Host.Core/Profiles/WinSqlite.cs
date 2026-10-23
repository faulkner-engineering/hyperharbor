using System.Runtime.InteropServices;
using System.Text;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>SQLite reported an error.</summary>
public sealed class SqliteException(string message) : Exception(message);

/// <summary>
/// A small reader over winsqlite3.dll, the SQLite that ships with Windows 10 and later (System32), so reading
/// the winget package index needs no extra library. Only what the index needs: open, prepare, bind text and
/// integers, step, and read text columns.
/// </summary>
public sealed class WinSqlite : IDisposable
{
    private const int Ok = 0;
    private const int Row = 100;
    private const int Done = 101;
    private const int OpenReadOnly = 0x1;
    private const int OpenReadWrite = 0x2;
    private const int OpenCreate = 0x4;
    private static readonly IntPtr Transient = new(-1);

    private IntPtr _db;

    private WinSqlite(IntPtr db)
    {
        _db = db;
    }

    /// <exception cref="SqliteException">The file could not be opened.</exception>
    public static WinSqlite Open(string path, bool readOnly = true)
    {
        var flags = readOnly ? OpenReadOnly : OpenReadWrite | OpenCreate;
        var name = Encoding.UTF8.GetBytes(path + "\0");
        var result = NativeMethods.sqlite3_open_v2(name, out var db, flags, IntPtr.Zero);
        if (result != Ok)
        {
            var message = db == IntPtr.Zero ? $"SQLite error {result}" : Message(db);
            NativeMethods.sqlite3_close_v2(db);
            throw new SqliteException($"The database could not be opened: {message}");
        }

        return new WinSqlite(db);
    }

    /// <summary>Runs a statement that returns no rows.</summary>
    public void Execute(string sql, params object[] parameters)
    {
        foreach (var _ in Query(sql, 0, parameters))
        {
        }
    }

    /// <summary>The rows of a query, each as its first <paramref name="columns"/> columns' text (null for NULL).</summary>
    /// <param name="parameters">Bound in order as ?1, ?2, ...: strings as text, ints and longs as integers.</param>
    public IEnumerable<string?[]> Query(string sql, int columns, params object[] parameters)
    {
        if (NativeMethods.sqlite3_prepare16_v2(_db, sql, -1, out var statement, IntPtr.Zero) != Ok)
        {
            throw new SqliteException(Message(_db));
        }

        try
        {
            for (var index = 0; index < parameters.Length; index++)
            {
                var bound = parameters[index] switch
                {
                    string text => NativeMethods.sqlite3_bind_text16(statement, index + 1, text, -1, Transient),
                    int number => NativeMethods.sqlite3_bind_int64(statement, index + 1, number),
                    long number => NativeMethods.sqlite3_bind_int64(statement, index + 1, number),
                    _ => throw new ArgumentException($"Parameter {index + 1} has an unsupported type."),
                };
                if (bound != Ok)
                {
                    throw new SqliteException(Message(_db));
                }
            }

            while (true)
            {
                var step = NativeMethods.sqlite3_step(statement);
                if (step == Done)
                {
                    yield break;
                }

                if (step != Row)
                {
                    throw new SqliteException(Message(_db));
                }

                var row = new string?[columns];
                for (var column = 0; column < columns; column++)
                {
                    var text = NativeMethods.sqlite3_column_text16(statement, column);
                    row[column] = text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text);
                }

                yield return row;
            }
        }
        finally
        {
            NativeMethods.sqlite3_finalize(statement);
        }
    }

    public void Dispose()
    {
        if (_db != IntPtr.Zero)
        {
            NativeMethods.sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }
    }

    private static string Message(IntPtr db) => Marshal.PtrToStringUni(NativeMethods.sqlite3_errmsg16(db)) ?? "unknown SQLite error";

    private static class NativeMethods
    {
        private const string Library = "winsqlite3.dll";

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_close_v2(IntPtr db);

        [DllImport(Library, ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport(Library, ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int sqlite3_bind_text16(IntPtr statement, int index, string value, int bytes, IntPtr destructor);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_step(IntPtr statement);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern IntPtr sqlite3_column_text16(IntPtr statement, int column);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_finalize(IntPtr statement);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern IntPtr sqlite3_errmsg16(IntPtr db);
    }
}
