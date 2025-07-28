namespace ion.compiler;

using ion.syntax;
using runtime;

public class TransformStage(CompilationContext context) : CompilationStage(context)
{
    public void Run(IEnumerable<IonFileSyntax> modules)
    {

    }

    private IonModule TransformFile(IonFileSyntax file)
    {
        var module = new IonModule()
        {
            Path = file.file.FullName,
            Name = file.Name,
            Imports = file.useSyntaxes.Select(s => s.Path).ToList(),
            
        }
    }


    private IonType CompileType(IonMessageSyntax syntax)
    {
        var attributes = new List<IonAttributeInstance>();


        foreach (var attribute in syntax.Attributes)
        {
            attributes.Add(new IonAttributeInstance(attribute.Name, attribute.));
        }


        var type = new IonType(syntax.Name, [], []);
    }

    private IonAttributeInstance CompileAttributeInstance(IonAttributeSyntax syntax)
    {
        var type = context.ResolveAttributeType(syntax.Name);


        foreach (var argument in type.arguments)
        {
            if (!argument.IsBuiltin)
                throw new Exception($"Not supported");


        }

    }
}