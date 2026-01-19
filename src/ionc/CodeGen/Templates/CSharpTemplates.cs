namespace ion.compiler.CodeGen.Templates;

/// <summary>
/// C# шаблоны для генерации форматтеров, executor'ов и клиентов.
/// </summary>
public sealed class CSharpTemplateProvider : ITemplateProvider
{
    // ═══════════════════════════════════════════════════════════════════
    // FORMATTER TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string FormatterTemplate =>
        """
        {generatedAttr}
        public sealed class Ion_{typeName}_Formatter : IonFormatter<{typeName}>
        {
            {generatedAttr}
            public {typeName} Read(CborReader reader)
            {
                var arraySize = reader.ReadStartArray() ?? throw new Exception("undefined len array not allowed");
                {readFields}
                reader.ReadEndArrayAndSkip(arraySize - {fieldsCount});
                return new({ctorArgs});
            }
            
            {generatedAttr}
            public void Write(CborWriter writer, {typeName} value)
            {
                writer.WriteStartArray({fieldsCount});
                {writeFields}
                writer.WriteEndArray();
            }
        }
        """;

    public string FormatterUnionCaseTemplate => FormatterTemplate;

    public string FormatterEnumTemplate =>
        """
        {generatedAttr}
        public sealed class Ion_{typeName}_Formatter : IonFormatter<{typeName}>
        {
            {generatedAttr}
            public {typeName} Read(CborReader reader)
            {
                 return ({typeName})({readExpr}.Read(reader));
            }
            
            {generatedAttr}
            public void Write(CborWriter writer, {typeName} value)
            {
                var casted = ({baseTypeName})value;
                {writeExpr}
            }
        }
        """;

    public string FormatterFlagsTemplate => FormatterEnumTemplate;

    public string FormatterUnionTemplate =>
        """
        {generatedAttr}
        public sealed class Ion_I{unionName}_Formatter : IonFormatter<I{unionName}>
        {
            public I{unionName} Read(CborReader reader)
            {
                var arraySize = reader.ReadStartArray() ?? throw new Exception("undefined len array not allowed");
                var unionIndex = reader.ReadUInt32();
                I{unionName} result;
                if (false) {}
                {readCases}
                else
                    throw new InvalidOperationException();
                reader.ReadEndArray();
                return result;
            }

            public void Write(CborWriter writer, I{unionName} value)
            {
                writer.WriteStartArray(2);
                writer.WriteUInt32(value.UnionIndex);

                if (false) {}
                {writeCases}    
                else
                    throw new InvalidOperationException();
                writer.WriteEndArray();    
            }
        }
        """;

    public string FormatterUnionReadCaseTemplate =>
        """
                else if (unionIndex == {caseIndex})
                    result = IonFormatterStorage<{caseTypeName}>.Read(reader);
        """;

    public string FormatterUnionWriteCaseTemplate =>
        """
                else if (value is {caseTypeName} n_{caseIndex})
                {
                    if (n_{caseIndex}.UnionIndex != {caseIndex})
                        throw new InvalidOperationException();
                    IonFormatterStorage<{caseTypeName}>.Write(writer, n_{caseIndex});
                }
        """;

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE CLIENT TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string ServiceClientClassTemplate =>
        """
        {generatedAttr}
        public sealed class Ion_{serviceName}_ClientImpl(IonClientContext context) : I{serviceName}
        {
            {methodInfoDecls}

            {methods}
        }
        """;

    public string ServiceClientMethodTemplate =>
        """
            {generatedAttr}
            public async Task<{returnType}> {methodName}({args})
            {
                var req = new IonRequest(context, typeof(I{serviceName}), {methodName}_Ref.Value);
            
                var writer = new CborWriter();
                
                const int argsSize = {argsCount};
            
                writer.WriteStartArray(argsSize);
                
                {writeArgs}
                
                writer.WriteEndArray();
            
                return await req.CallAsync<{returnType}>(writer.Encode(), ct: ct);
            }
        """;

    public string ServiceClientMethodVoidTemplate =>
        """
            {generatedAttr}
            public async Task {methodName}({args})
            {
                var req = new IonRequest(context, typeof(I{serviceName}), {methodName}_Ref.Value);

                var writer = new CborWriter();
                
                const int argsSize = {argsCount};

                writer.WriteStartArray(argsSize);
                
                {writeArgs}
                
                writer.WriteEndArray();

                await req.CallAsync(writer.Encode(), ct: ct);
            }
        """;

    public string ServiceClientMethodStreamTemplate =>
        """
            public IAsyncEnumerable<{returnType}> {methodName}({args})
            {
                var ws = new IonWsClient(context, typeof(I{serviceName}), {methodName}_Ref.Value);
            
                var writer = new CborWriter();

                const int argsSize = {argsCount};
                
                writer.WriteStartArray(argsSize);
                
                {writeArgs}
                
                writer.WriteEndArray();
            
                return {streamCall};
            }
        """;

    public string? ServiceClientMethodArrayTemplate =>
        """
            {generatedAttr}
            public async Task<{returnType}> {methodName}({args})
            {
                var req = new IonRequest(context, typeof(I{serviceName}), {methodName}_Ref.Value);
            
                var writer = new CborWriter();
                
                const int argsSize = {argsCount};
            
                writer.WriteStartArray(argsSize);
                
                {writeArgs}
                
                writer.WriteEndArray();
            
                return await req.CallAsyncWithArray<{returnTypeUnwrapped}>(writer.Encode(), ct: ct);
            }
        """;

    public string? ServiceClientMethodNullableTemplate =>
        """
            {generatedAttr}
            public async Task<{returnType}> {methodName}({args})
            {
                var req = new IonRequest(context, typeof(I{serviceName}), {methodName}_Ref.Value);
            
                var writer = new CborWriter();
                
                const int argsSize = {argsCount};
            
                writer.WriteStartArray(argsSize);
                
                {writeArgs}
                
                writer.WriteEndArray();
            
                return await req.CallAsyncNullable<{returnTypeUnwrapped}>(writer.Encode(), ct: ct);
            }
        """;

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE EXECUTOR TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string ServiceExecutorClassTemplate =>
        """
        public sealed class Ion_{serviceName}_ServiceExecutor(AsyncServiceScope scope) : {interfaces}
        {
            {methods}
            
            {routerBody}
            
            {streamRouterBody}
            
            private static readonly string[] __allowedStreamingMethods = [
                {allowedStreamMethods}
            ];
            
            public bool IsAllowInputStream(string methodName) => __allowedStreamingMethods.Contains(methodName);
        }
        """;

    public string ServiceExecutorMethodTemplate =>
        """
            {generatedAttr}
            public async Task {methodName}_Execute(CborReader reader, CborWriter writer, CancellationToken ct = default)
            {
                var service = scope.ServiceProvider.GetRequiredService<I{serviceName}>();
            
                const int argumentSize = {argsCount};
            
                var arraySize = reader.ReadStartArray() ?? throw new Exception("undefined len array not allowed");
            
                {readArgs}
            
                reader.ReadEndArrayAndSkip(arraySize - argumentSize);
            
                var result = await service.{methodName}({captureArgs});
                
                {writeResult}
            }
        """;

    public string ServiceExecutorMethodVoidTemplate =>
        """
            {generatedAttr}
            public async Task {methodName}_Execute(CborReader reader, CborWriter writer, CancellationToken ct = default)
            {
                var service = scope.ServiceProvider.GetRequiredService<I{serviceName}>();
            
                const int argumentSize = {argsCount};
            
                var arraySize = reader.ReadStartArray() ?? throw new Exception("undefined len array not allowed");
            
                {readArgs}
            
                reader.ReadEndArrayAndSkip(arraySize - argumentSize);
            
                await service.{methodName}({captureArgs});
            }
        """;

    public string ServiceExecutorMethodStreamTemplate =>
        """
            public async IAsyncEnumerable<Memory<byte>> {methodName}_Execute(CborReader reader, IAsyncEnumerable<ReadOnlyMemory<byte>>? inputStream, CancellationToken ct = default)
            {
                var service = scope.ServiceProvider.GetRequiredService<I{serviceName}>();

                const int argumentSize = {argsCount};
                
                {inputStreamCast}

                var arraySize = reader.ReadStartArray() ?? throw new Exception("undefined len array not allowed");
                    
                {readArgs}

                reader.ReadEndArrayAndSkip(arraySize - argumentSize);

                await foreach (var e in service.{methodName}({captureArgs}))
                {
                    var writer = new CborWriter();

                    IonFormatterStorage<{returnType}>.Write(writer, e);

                    var mem = MemoryPool<byte>.Shared.Rent(writer.BytesWritten);

                    writer.Encode(mem.Memory.Span);

                    yield return mem.Memory;

                    mem.Dispose();
                }
            }
        """;

    public string ServiceExecutorRouterTemplate =>
        """
            public Task RouteExecuteAsync(string methodName, CborReader reader, CborWriter writer, CancellationToken ct = default)
            {
                {branches}
                
                throw new InvalidOperationException("no method defined");
            }
        """;

    public string ServiceExecutorStreamRouterTemplate =>
        """
            public IAsyncEnumerable<Memory<byte>> StreamRouteExecuteAsync(string methodName, CborReader reader, IAsyncEnumerable<ReadOnlyMemory<byte>>? inputStream, [EnumeratorCancellation] CancellationToken ct)
            {
                {branches}
                
                throw new InvalidOperationException("no method defined");
            }
        """;

    public string ServiceExecutorBranchTemplate =>
        """
                if (methodName.Equals("{methodName}", StringComparison.InvariantCultureIgnoreCase))
                    return {methodName}_Execute(reader, {executorArgs});
        """;

    public string? InputStreamCastTemplate =>
        """
        var inputStreamCasted = inputStream is null
        ? null
        : inputStream.Select(bytes =>
        {
            var reader = new CborReader(bytes);
            var arr = reader.ReadStartArray();
            var result = IonFormatterStorage<{inputStreamType}>.Read(reader);
            reader.ReadEndArray();

            return result;
        });
        """;

    // ═══════════════════════════════════════════════════════════════════
    // MODULE INIT TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string ModuleInitTemplate =>
        """
        {generatedAttr}
        internal static class IonProjectFormatterStorageModuleInit
        {
            [ModuleInitializer]
            internal static void Init()
            {
                {registrations}
            }
        }
        """;

    // ═══════════════════════════════════════════════════════════════════
    // PROXY TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string? ClientProxyTemplate => null; // C# doesn't use proxy pattern like TS
}
