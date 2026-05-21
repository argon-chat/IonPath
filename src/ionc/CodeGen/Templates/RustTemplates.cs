namespace ion.compiler.CodeGen.Templates;

/// <summary>
/// Rust шаблоны для генерации форматтеров и клиентов.
/// </summary>
public sealed class RustTemplateProvider : ITemplateProvider
{
    // ═══════════════════════════════════════════════════════════════════
    // FORMATTER TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string FormatterTemplate =>
        """
        impl IonFormat for {typeName} {
            fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
                let len = d.array()?.ok_or(IonError::IndefiniteArray)?;
                {readFields}
                ion_rustcore::formatter::skip_remaining(d, len, {fieldsCount})?;
                Ok(Self { {ctorArgs} })
            }

            fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
                e.array({fieldsCount})?;
                {writeFields}
                Ok(())
            }
        }
        """;

    public string FormatterUnionCaseTemplate =>
        """
        impl IonFormat for {typeName} {
            fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
                let len = d.array()?.ok_or(IonError::IndefiniteArray)?;
                {readFields}
                ion_rustcore::formatter::skip_remaining(d, len, {fieldsCount})?;
                Ok(Self { {ctorArgs} })
            }

            fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
                e.array({fieldsCount})?;
                {writeFields}
                Ok(())
            }
        }
        """;

    public string FormatterEnumTemplate =>
        """
        impl IonFormat for {typeName} {
            fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
                let raw = {readExpr};
                Self::try_from(raw).map_err(|_| IonError::InvalidEnum(raw as i64))
            }

            fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
                (*self as {baseTypeName}).ion_write(e)
            }
        }

        impl TryFrom<{baseTypeName}> for {typeName} {
            type Error = ();
            fn try_from(value: {baseTypeName}) -> Result<Self, Self::Error> {
                // Safety: check all valid discriminants
                {enumVariantCheck}
            }
        }
        """;

    public string FormatterFlagsTemplate =>
        """
        impl IonFormat for {typeName} {
            fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
                let raw = {readExpr};
                Ok(Self(raw))
            }

            fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
                self.0.ion_write(e)
            }
        }
        """;

    public string FormatterUnionTemplate =>
        """
        impl IonFormat for {unionName} {
            fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
                d.array()?;
                let union_index = d.u32()?;
                let value = match union_index {
                    {readCases}
                    _ => return Err(IonError::InvalidUnionIndex(union_index)),
                };
                Ok(value)
            }

            fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
                e.array(2)?;
                e.u32(self.union_index())?;
                match self {
                    {writeCases}
                }
                Ok(())
            }
        }
        """;

    public string FormatterUnionReadCaseTemplate =>
        """
                    {caseIndex} => {unionName}::{caseTypeName}(<{caseTypeName} as IonFormat>::ion_read(d)?),
        """;

    public string FormatterUnionWriteCaseTemplate =>
        """
                    Self::{caseTypeName}(v) => v.ion_write(e)?,
        """;

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE CLIENT TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string ServiceClientClassTemplate =>
        """
        pub struct {serviceName}Client {
            ctx: ion_rustcore::IonClientContext,
        }

        impl ion_rustcore::FromContext for {serviceName}Client {
            fn from_context(ctx: ion_rustcore::IonClientContext) -> Self {
                Self { ctx }
            }
        }

        impl {serviceName}Client {
            {methods}
        }
        """;

    public string ServiceClientMethodTemplate =>
        """
            pub async fn {methodName}(&self, {args}) -> Result<{returnType}, ion_rustcore::IonError> {
                let mut e = ion_rustcore::Encoder::new(Vec::new());
                e.array({argsCount})?;
                {writeArgs}
                let buf = e.into_writer();
                let req = ion_rustcore::IonRequest::new(&self.ctx, "I{serviceName}", "{originalMethodName}");
                req.call::<{returnType}>(&buf).await
            }
        """;

    public string ServiceClientMethodVoidTemplate =>
        """
            pub async fn {methodName}(&self, {args}) -> Result<(), ion_rustcore::IonError> {
                let mut e = ion_rustcore::Encoder::new(Vec::new());
                e.array({argsCount})?;
                {writeArgs}
                let buf = e.into_writer();
                let req = ion_rustcore::IonRequest::new(&self.ctx, "I{serviceName}", "{originalMethodName}");
                req.call_void(&buf).await
            }
        """;

    public string? ServiceClientMethodNullableTemplate =>
        """
            pub async fn {methodName}(&self, {args}) -> Result<Option<{returnTypeInner}>, ion_rustcore::IonError> {
                let mut e = ion_rustcore::Encoder::new(Vec::new());
                e.array({argsCount})?;
                {writeArgs}
                let buf = e.into_writer();
                let req = ion_rustcore::IonRequest::new(&self.ctx, "I{serviceName}", "{originalMethodName}");
                req.call_nullable::<{returnTypeInner}>(&buf).await
            }
        """;

    public string? ServiceClientMethodArrayTemplate => null;

    public string ServiceClientMethodStreamTemplate =>
        """
            pub async fn {methodName}(&self, {args}) -> Result<ion_rustcore::IonWsStream<{returnType}>, ion_rustcore::IonError> {
                let mut e = ion_rustcore::Encoder::new(Vec::new());
                e.array({argsCount})?;
                {writeArgs}
                let buf = e.into_writer();
                ion_rustcore::IonWsStream::open(&self.ctx, "I{serviceName}", "{originalMethodName}", &buf).await
            }
        """;

    public string ServiceClientMethodDuplexStreamTemplate =>
        """
            pub async fn {methodName}(&self, {args}) -> Result<ion_rustcore::IonWsDuplexStream<{inputType}, {returnType}>, ion_rustcore::IonError> {
                let mut e = ion_rustcore::Encoder::new(Vec::new());
                e.array({argsCount})?;
                {writeArgs}
                let buf = e.into_writer();
                ion_rustcore::IonWsDuplexStream::open(&self.ctx, "I{serviceName}", "{originalMethodName}", &buf).await
            }
        """;

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE EXECUTOR TEMPLATES (Not used in Rust client)
    // ═══════════════════════════════════════════════════════════════════

    public string ServiceExecutorClassTemplate => throw new NotSupportedException("Rust is client-only");
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

    public string ModuleInitTemplate => "";

    // ═══════════════════════════════════════════════════════════════════
    // PROXY TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public string? ClientProxyTemplate => null;
}
