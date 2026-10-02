using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

// Lettore dBASE III minimale (file .dbf, per esempio i dati dei progetti di vecchi terminali operatore).
// Serve solo a leggere: nessuna scrittura, nessuna interpretazione.
class DbfDump
{
    class Campo { public string Nome; public char Tipo; public int Lung; }

    static void Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("uso: DbfDump <file.dbf> [--campi]"); return; }
        byte[] b = File.ReadAllBytes(args[0]);

        int nRec = BitConverter.ToInt32(b, 4);
        int hLen = BitConverter.ToInt16(b, 8);
        int rLen = BitConverter.ToInt16(b, 10);

        var campi = new List<Campo>();
        for (int p = 32; p < hLen - 1 && b[p] != 0x0D; p += 32)
        {
            var c = new Campo();
            int n = 0; while (n < 11 && b[p + n] != 0) n++;
            c.Nome = Encoding.ASCII.GetString(b, p, n);
            c.Tipo = (char)b[p + 11];
            c.Lung = b[p + 16];
            campi.Add(c);
        }

        if (args.Length > 1 && args[1] == "--campi")
        {
            Console.WriteLine("record: " + nRec + "   lunghezza record: " + rLen);
            foreach (var c in campi) Console.WriteLine("  " + c.Nome + "  " + c.Tipo + "  " + c.Lung);
            return;
        }

        var hdr = new StringBuilder();
        foreach (var c in campi) { if (hdr.Length > 0) hdr.Append(";"); hdr.Append(c.Nome); }
        Console.WriteLine(hdr.ToString());

        var enc = Encoding.GetEncoding(437);
        for (int r = 0; r < nRec; r++)
        {
            int off = hLen + r * rLen;
            if (off + rLen > b.Length) break;
            if (b[off] == 0x2A) continue;            // record cancellato
            var sb = new StringBuilder();
            int p = off + 1;
            for (int i = 0; i < campi.Count; i++)
            {
                string v = enc.GetString(b, p, campi[i].Lung).TrimEnd();
                p += campi[i].Lung;
                if (i > 0) sb.Append(";");
                sb.Append(v.Replace(";", ","));
            }
            Console.WriteLine(sb.ToString());
        }
    }
}
