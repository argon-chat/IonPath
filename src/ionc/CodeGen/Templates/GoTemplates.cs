namespace ion.compiler.CodeGen.Templates;

/// <summary>
/// Go templates for generating formatters, routers, and clients.
/// The templates follow patterns from ion.server.go and ion.webcore.go.
/// </summary>
public sealed class GoTemplateProvider : ITemplateProvider
{
    // ═══════════════════════════════════════════════════════════════════
    // FORMATTER TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string FormatterTemplate =>
        """
        	ionwebcore.RegisterFunc[{typeName}](
        		func(r *cbor.CborReader) ({typeName}, error) {
        			if _, err := r.ReadStartArray(); err != nil {
        				return {typeName}{}, err
        			}
        {readFields}
        			if err := r.ReadEndArray(); err != nil {
        				return {typeName}{}, err
        			}
        			return {typeName}{{ctorArgs}}, nil
        		},
        		func(w *cbor.CborWriter, v {typeName}) error {
        			if err := w.WriteStartArray({fieldsCount}); err != nil {
        				return err
        			}
        {writeFields}
        			return w.WriteEndArray()
        		},
        	)
        """;

    public string FormatterUnionCaseTemplate => FormatterTemplate;

    public string FormatterEnumTemplate =>
        """
        	ionwebcore.RegisterFunc[{typeName}](
        		func(r *cbor.CborReader) ({typeName}, error) {
        			val, err := ionwebcore.Read[{baseTypeName}](r)
        			if err != nil {
        				return 0, err
        			}
        			return {typeName}(val), nil
        		},
        		func(w *cbor.CborWriter, v {typeName}) error {
        			return ionwebcore.Write(w, {baseTypeName}(v))
        		},
        	)
        """;
    public string FormatterFlagsTemplate => FormatterEnumTemplate;

    public string FormatterUnionTemplate =>
        """
        func init() {
        	ionwebcore.RegisterFunc[I{unionName}](
        		func(r *cbor.CborReader) (I{unionName}, error) {
        			if _, err := r.ReadStartArray(); err != nil {
        				return nil, err
        			}
        			unionIndex, err := r.ReadUInt32()
        			if err != nil {
        				return nil, err
        			}
        			
        			var result I{unionName}
        			switch unionIndex {
        			{readCases}
        			default:
        				return nil, fmt.Errorf("unknown union index: %d", unionIndex)
        			}
        			
        			if err := r.ReadEndArray(); err != nil {
        				return nil, err
        			}
        			return result, nil
        		},
        		func(w *cbor.CborWriter, v I{unionName}) error {
        			if err := w.WriteStartArray(2); err != nil {
        				return err
        			}
        			if err := w.WriteUInt32(v.UnionIndex()); err != nil {
        				return err
        			}
        			
        			switch v.UnionIndex() {
        			{writeCases}
        			default:
        				return fmt.Errorf("unknown union index: %d", v.UnionIndex())
        			}
        			
        			return w.WriteEndArray()
        		},
        	)
        }
        """;

    public string FormatterUnionReadCaseTemplate =>
        """
        		case {caseIndex}:
        			val, err := ionwebcore.Read[{caseTypeName}](r)
        			if err != nil {
        				return nil, err
        			}
        			result = val
        """;

    public string FormatterUnionWriteCaseTemplate =>
        """
        		case {caseIndex}:
        			return ionwebcore.Write(w, v.({caseTypeName}))
        """;

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE ROUTER TEMPLATES (Server-side)
    // ═══════════════════════════════════════════════════════════════════

    public string ServiceExecutorClassTemplate =>
        """
        // {serviceName}Router routes calls to {serviceName} methods.
        type {serviceName}Router struct {
        	service I{serviceName}
        }

        // New{serviceName}Router creates a new router.
        func New{serviceName}Router(service I{serviceName}) *{serviceName}Router {
        	return &{serviceName}Router{service: service}
        }

        // RouteExecute implements ionserver.ServiceRouter.
        func (r *{serviceName}Router) RouteExecute(methodName string, reader *cbor.CborReader, writer *cbor.CborWriter) error {
        	argCount, err := reader.ReadStartArray()
        	if err != nil {
        		return fmt.Errorf("failed to read args array: %w", err)
        	}

        	switch methodName {
        	{routerBranches}
        	default:
        		return &ionwebcore.IonRequestError{
        			ProtocolError: ionwebcore.IonProtocolError{
        				Code:    "METHOD_NOT_FOUND",
        				Message: fmt.Sprintf("Method %s not found", methodName),
        			},
        		}
        	}
        	_ = argCount // suppress unused warning
        }
        """;

    public string ServiceExecutorMethodTemplate =>
        """
        	case "{methodName}":
        		{readArgs}
        		if err := reader.ReadEndArray(); err != nil {
        			return err
        		}
        		result, err := r.service.{methodName}({captureArgs})
        		if err != nil {
        			return err
        		}
        		return ionwebcore.Write(writer, result)
        """;

    public string ServiceExecutorMethodVoidTemplate =>
        """
        	case "{methodName}":
        		{readArgs}
        		if err := reader.ReadEndArray(); err != nil {
        			return err
        		}
        		return r.service.{methodName}({captureArgs})
        """;

    public string ServiceExecutorMethodStreamTemplate =>
        """
        	case "{methodName}":
        		// Streaming methods are handled by StreamRouter
        		return fmt.Errorf("streaming method %s should use StreamRouter", methodName)
        """;

    public string ServiceExecutorRouterTemplate => ""; // Included in ServiceExecutorClassTemplate
    public string ServiceExecutorStreamRouterTemplate =>
        """
        // {serviceName}StreamRouter handles streaming calls.
        type {serviceName}StreamRouter struct {
        	service I{serviceName}
        }

        // New{serviceName}StreamRouter creates a new stream router.
        func New{serviceName}StreamRouter(service I{serviceName}) *{serviceName}StreamRouter {
        	return &{serviceName}StreamRouter{service: service}
        }

        // IsInputStreamAllowed implements ionserver.StreamRouter.
        func (r *{serviceName}StreamRouter) IsInputStreamAllowed(methodName string) bool {
        	switch methodName {
        	{inputStreamAllowedCases}
        	default:
        		return false
        	}
        }

        // StreamRouteExecute implements ionserver.StreamRouter.
        func (r *{serviceName}StreamRouter) StreamRouteExecute(
        	ctx context.Context,
        	methodName string,
        	initialArgs *cbor.CborReader,
        	inputStream <-chan []byte,
        ) (<-chan []byte, error) {
        	switch methodName {
        	{streamRouterBranches}
        	default:
        		return nil, &ionwebcore.IonRequestError{
        			ProtocolError: ionwebcore.IonProtocolError{
        				Code:    "METHOD_NOT_FOUND",
        				Message: fmt.Sprintf("Streaming method %s not found", methodName),
        			},
        		}
        	}
        }
        """;

    public string ServiceExecutorBranchTemplate => ""; // Included in method templates

    public string? InputStreamCastTemplate => null; // Go handles this differently

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE CLIENT TEMPLATES (Not typically needed for Go server)
    // ═══════════════════════════════════════════════════════════════════

    public string ServiceClientClassTemplate =>
        """
        // {serviceName}Client is a client for I{serviceName}.
        type {serviceName}Client struct {
        	client *ionwebcore.IonClient
        }

        // New{serviceName}Client creates a new client.
        func New{serviceName}Client(client *ionwebcore.IonClient) *{serviceName}Client {
        	return &{serviceName}Client{client: client}
        }

        {methods}
        """;

    public string ServiceClientMethodTemplate =>
        """
        func (c *{serviceName}Client) {methodName}(ctx context.Context, {args}) ({returnType}, error) {
        	writer := cbor.NewCborWriter()
        	_ = writer.WriteStartArray({argsCount})
        	{writeArgs}
        	_ = writer.WriteEndArray()
        	
        	resp, err := c.client.Call(ctx, "I{serviceName}", "{methodName}", writer.Bytes())
        	if err != nil {
        		var zero {returnType}
        		return zero, err
        	}
        	
        	reader := cbor.NewCborReader(resp)
        	return ionwebcore.Read[{returnType}](reader)
        }
        """;

    public string ServiceClientMethodVoidTemplate =>
        """
        func (c *{serviceName}Client) {methodName}(ctx context.Context, {args}) error {
        	writer := cbor.NewCborWriter()
        	_ = writer.WriteStartArray({argsCount})
        	{writeArgs}
        	_ = writer.WriteEndArray()
        	
        	_, err := c.client.Call(ctx, "I{serviceName}", "{methodName}", writer.Bytes())
        	return err
        }
        """;

    public string ServiceClientMethodStreamTemplate =>
        """
        func (c *{serviceName}Client) {methodName}(ctx context.Context, {args}) (<-chan {returnType}, error) {
        	writer := cbor.NewCborWriter()
        	_ = writer.WriteStartArray({argsCount})
        	{writeArgs}
        	_ = writer.WriteEndArray()
        	
        	return c.client.CallStream[{returnType}](ctx, "I{serviceName}", "{methodName}", writer.Bytes())
        }
        """;

    public string? ServiceClientMethodArrayTemplate => null;
    public string? ServiceClientMethodNullableTemplate => null;

    // ═══════════════════════════════════════════════════════════════════
    // MODULE INIT TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string ModuleInitTemplate =>
        """
        // This file registers all formatters for the Ion types.
        // It is automatically generated by ionc.

        {registrations}
        """;

    // ═══════════════════════════════════════════════════════════════════
    // PROXY TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string? ClientProxyTemplate => null; // Go doesn't use proxy pattern
}
