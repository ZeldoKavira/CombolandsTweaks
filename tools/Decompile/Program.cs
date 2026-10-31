using System;
using System.IO;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;

// Decompiles whole game assemblies into readable C# source trees, one file per type.
//   Decompile <outDir> <assembly.dll>...
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: Decompile <outDir> <assembly.dll>...");
            return 1;
        }

        string outRoot = args[0];
        for (int i = 1; i < args.Length; i++)
        {
            string asm = args[i];
            string outDir = Path.Combine(outRoot, Path.GetFileNameWithoutExtension(asm));
            if (Directory.Exists(outDir))
                Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);

            var module = new PEFile(asm);
            var resolver = new UniversalAssemblyResolver(asm, false, module.DetectTargetFrameworkId());
            resolver.AddSearchDirectory(Path.GetDirectoryName(asm));
            var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
            new WholeProjectDecompiler(settings, resolver, null, null, null).DecompileProject(module, outDir);
            Console.WriteLine($"{Path.GetFileName(asm)} -> {outDir} ({Directory.GetFiles(outDir, "*.cs", SearchOption.AllDirectories).Length} files)");
        }
        return 0;
    }
}
