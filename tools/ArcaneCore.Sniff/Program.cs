using ArcaneCore.Sniff;

if (args.Length != 2 || args[0] != "decode")
{
    Console.Error.WriteLine("usage: arcane-sniff decode <file>");
    return 2;
}

try { return await SniffDecoder.DecodeAsync(args[1], Console.Out); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
{
    Console.Error.WriteLine("arcane-sniff: " + ex.Message);
    return 1;
}
