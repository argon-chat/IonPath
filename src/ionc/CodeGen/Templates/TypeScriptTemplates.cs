namespace ion.compiler.CodeGen.Templates;

/// <summary>
/// TypeScript шаблоны для генерации форматтеров и клиентов.
/// </summary>
public sealed class TypeScriptTemplateProvider : ITemplateProvider
{
    // ═══════════════════════════════════════════════════════════════════
    // FORMATTER TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string FormatterTemplate =>
        """
        IonFormatterStorage.register("{typeName}", {
          read(reader: CborReader): {typeName} {
            const arraySize = reader.readStartArray() ?? (() => { throw new Error("undefined len array not allowed") })();
            {readFields}
            reader.readEndArrayAndSkip(arraySize - {fieldsCount});
            return { {ctorArgs} };
          },
          write(writer: CborWriter, value: {typeName}): void {
            writer.writeStartArray({fieldsCount});
            {writeFields}
            writer.writeEndArray();
          }
        });
        """;

    public string FormatterUnionCaseTemplate =>
        """
        IonFormatterStorage.register("{typeName}", {
          read(reader: CborReader): {typeName} {
            const arraySize = reader.readStartArray() ?? (() => { throw new Error("undefined len array not allowed") })();
            {readFields}
            reader.readEndArrayAndSkip(arraySize - {fieldsCount});
            return new {typeName}({ctorArgs});
          },
          write(writer: CborWriter, value: {typeName}): void {
            writer.writeStartArray({fieldsCount});
            {writeFields}
            writer.writeEndArray();
          }
        });
        """;

    public string FormatterEnumTemplate =>
        """
        IonFormatterStorage.register("{typeName}", {
          read(reader: CborReader): {typeName} {
            const num = ({readExpr}.read(reader))
            return {typeName}[num] !== undefined ? num as {typeName} : (() => {throw new Error('invalid enum type')})();
          },
          write(writer: CborWriter, value: {typeName}): void {
            const casted: {baseTypeName} = value;
            {writeExpr}
          }
        });
        """;

    public string FormatterFlagsTemplate =>
        """
        IonFormatterStorage.register("{typeName}", {
          read(reader: CborReader): {typeName} {
            const num = ({readExpr}.read(reader))
            return num as any;
          },
          write(writer: CborWriter, value: {typeName}): void {
            const casted: {baseTypeName} = value as any;
            {writeExpr}
          }
        });
        """;

    public string FormatterUnionTemplate =>
        """
        IonFormatterStorage.register("I{unionName}", {
          read(reader: CborReader): I{unionName} {
            reader.readStartArray();
            let value: I{unionName} = null as any;
            const unionIndex = reader.readUInt32();
            
            if (false)
            {}
            {readCases}
            else throw new Error();
          
            reader.readEndArray();
            return value!;
          },
          write(writer: CborWriter, value: I{unionName}): void {
            writer.writeStartArray(2);
            writer.writeUInt32(value.UnionIndex);
            if (false)
            {}
            {writeCases}  
            else throw new Error();
            writer.writeEndArray();
          }
        });
        """;

    public string FormatterUnionReadCaseTemplate =>
        """
            else if (unionIndex == {caseIndex})
              value = IonFormatterStorage.get<{caseTypeName}>("{caseTypeName}").read(reader);
        """;

    public string FormatterUnionWriteCaseTemplate =>
        """
            else if (value.UnionIndex == {caseIndex}) {
                IonFormatterStorage.get<{caseTypeName}>("{caseTypeName}").write(writer, value as {caseTypeName});
            }
        """;

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE CLIENT TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string ServiceClientClassTemplate =>
        """
        export class {serviceName}_Executor extends ServiceExecutor<I{serviceName}> implements I{serviceName} {
          constructor(public ctx: IonClientContext, private signal: AbortSignal) {
              super();
          }

          {methods}
        }

        IonFormatterStorage.registerClientExecutor<I{serviceName}>('{serviceName}', {serviceName}_Executor);
        """;

    public string ServiceClientMethodTemplate =>
        """
          async {methodName}({args}): Promise<{returnType}> {
            const req = new IonRequest(this.ctx, "I{serviceName}", "{methodName}");
                  
            const writer = new CborWriter();
              
            writer.writeStartArray({argsCount});
                  
            {writeArgs}
              
            writer.writeEndArray();
                  
            return await req.callAsyncT<{returnType}>("{returnType}", writer.data, this.signal);
          }
        """;

    public string ServiceClientMethodVoidTemplate =>
        """
          async {methodName}({args}): Promise<void> {
            const req = new IonRequest(this.ctx, "I{serviceName}", "{methodName}");
                  
            const writer = new CborWriter();
              
            writer.writeStartArray({argsCount});
                  
            {writeArgs}
              
            writer.writeEndArray();
                  
            await req.callAsync(writer.data, this.signal);
          }
        """;

    public string ServiceClientMethodStreamTemplate =>
        """
          {methodName}({args}): AsyncIterable<{returnType}> {
            const ws = new IonWsClient(this.ctx, "I{serviceName}", "{methodName}");
            
            const writer = new CborWriter();
            
            writer.writeStartArray({argsCount});
            
            {writeArgs}
            
            writer.writeEndArray();
            
            return {streamCall};
          }
        """;

    public string? ServiceClientMethodArrayTemplate => null;
    public string? ServiceClientMethodNullableTemplate => null;

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE EXECUTOR TEMPLATES (Not used in TypeScript)
    // ═══════════════════════════════════════════════════════════════════

    public string ServiceExecutorClassTemplate => throw new NotSupportedException("TypeScript doesn't support server executors");
    public string ServiceExecutorMethodTemplate => throw new NotSupportedException();
    public string ServiceExecutorMethodVoidTemplate => throw new NotSupportedException();
    public string ServiceExecutorMethodStreamTemplate => throw new NotSupportedException();
    public string ServiceExecutorRouterTemplate => throw new NotSupportedException();
    public string ServiceExecutorStreamRouterTemplate => throw new NotSupportedException();
    public string ServiceExecutorBranchTemplate => throw new NotSupportedException();
    public string? InputStreamCastTemplate => null;

    // ═══════════════════════════════════════════════════════════════════
    // MODULE INIT TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string ModuleInitTemplate => ""; // TypeScript registers formatters inline

    // ═══════════════════════════════════════════════════════════════════
    // PROXY TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string? ClientProxyTemplate =>
        """
        export function createClient(endpoint: string, interceptors: IonInterceptor[]) {
          const ctx = {
            baseUrl: endpoint,
            interceptors: interceptors
          } as IonClientContext;
          const controller = new AbortController();

          return new Proxy(
            {},
            {
              get(_target, propKey) {
                if (typeof propKey !== "string") return undefined;
        {serviceChecks}

                throw new Error(`${propKey} service is not defined`);
              },
            }
          ) as {
        {serviceTypes}
          };
        }
        """;
}
