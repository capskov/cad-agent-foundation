using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using var stream=File.OpenRead(args[0]);using var pe=new PEReader(stream);var reader=pe.GetMetadataReader();var assembly=reader.GetAssemblyDefinition();
Console.WriteLine(JsonSerializer.Serialize(new {name=reader.GetString(assembly.Name),mvid=reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString(),version=assembly.Version.ToString()}));
