namespace ion.compiler.CodeGen.Templates;

/// <summary>
/// Rust шаблоны для генерации форматтеров и клиентов.
/// </summary>
public sealed class RustTemplateProvider : ITemplateProvider
{
    // ═══════════════════════════════════════════════════════════════════
    // FORMATTER TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A message's <c>IonFormat</c> impl.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The field array is opened with <c>read_message_header</c> rather than a bare
    /// <c>d.array()?</c>, and its <c>DepthGuard</c> is bound as <c>_depth</c> for the whole body.
    /// <b>The guard is the point.</b> The collection helpers count the containers they open, but a
    /// message opens its own field array directly, so without this Rust counts <i>collections
    /// only</i> while C#'s <c>CborReader.CurrentDepth</c> and TypeScript's frame stack count every
    /// container — and the shared limit of 128 then means about 64 levels of a recursive message
    /// there and about 128 here.
    /// </para>
    /// <para>
    /// It is bound as <c>_depth</c> and not <c>_</c>: <c>DepthGuard</c> is <c>#[must_use]</c> and
    /// releases on drop, so <c>let _ =</c> would drop it immediately and count nothing.
    /// </para>
    /// </remarks>
    public string FormatterTemplate =>
        """
        impl IonFormat for {typeName} {
            fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
                let (len, _depth) = ion_rustcore::formatter::read_message_header(d, "{typeName}")?;
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

    /// <summary>
    /// A union case's <c>IonFormat</c> impl — a message like any other, including its
    /// <c>read_message_header</c> depth guard.
    /// </summary>
    public string FormatterUnionCaseTemplate =>
        """
        impl IonFormat for {typeName} {
            fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
                let (len, _depth) = ion_rustcore::formatter::read_message_header(d, "{typeName}")?;
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

    /// <summary>
    /// Unused on the Rust target: <c>ion_rustcore::ion_open_enum!</c> emits the <c>IonFormat</c>
    /// impl along with the type, so <see cref="RustCodeGenerator.GenerateEnumFormatter"/> emits
    /// nothing here.
    /// </summary>
    /// <remarks>
    /// What used to stand here was a closed reader — <c>Self::try_from(raw)</c> over a
    /// <c>TryFrom</c> impl that matched the declared discriminants and reached the variant with
    /// <c>Ok(unsafe { std::mem::transmute(x) })</c>. It made adding an enum member a breaking
    /// change on the wire (<c>IonError::InvalidEnum</c> for a value a newer peer declares), and it
    /// spelled a safe operation with <c>unsafe</c>. Both are gone with the open enum.
    /// </remarks>
    public string FormatterEnumTemplate => "";

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

    /// <summary>
    /// A union's <c>IonFormat</c> impl.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A union envelope is exactly <c>[index, payload]</c> — two items, in every revision of
    /// every union.</b> Growth happens inside the case payload, which is a message and skips its
    /// own tail. The old <c>d.array()?; let union_index = d.u32()?;</c> discarded the declared
    /// length, so a three-item envelope left its stray item in the stream and the <i>next field of
    /// the enclosing message</i> read it as its own value — <c>[[0, [1,"b"], 9], 5]</c> decoded
    /// with <c>n = 9</c>, silently. <c>read_union_envelope</c> rejects any length other than two
    /// with <c>IonError::UnionEnvelope</c>, before the index is read.
    /// </para>
    /// <para>
    /// The returned <c>DepthGuard</c> is bound as <c>_depth</c> for the whole body, for the reason
    /// given on <see cref="FormatterTemplate"/>: it is <c>#[must_use]</c>, and <c>let _ =</c>
    /// would drop it immediately and leave the envelope uncounted.
    /// </para>
    /// <para>
    /// The write side goes through <c>write_union_envelope</c>, which emits the same
    /// <c>array(2)</c> + <c>u32(index)</c> bytes it always did — encode is untouched.
    /// </para>
    /// </remarks>
    public string FormatterUnionTemplate =>
        """
        impl IonFormat for {unionName} {
            fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
                let (union_index, _depth) =
                    ion_rustcore::formatter::read_union_envelope(d, "{unionName}")?;
                let value = match union_index {
                    {readCases}
                    _ => return Err(IonError::InvalidUnionIndex(union_index)),
                };
                Ok(value)
            }

            fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
                ion_rustcore::formatter::write_union_envelope(e, self.union_index())?;
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
        {serviceDoc}pub struct {serviceName}Client {
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
        {methodDoc}    pub async fn {methodName}(&self, {args}) -> Result<{returnType}, ion_rustcore::IonError> {
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
        {methodDoc}    pub async fn {methodName}(&self, {args}) -> Result<(), ion_rustcore::IonError> {
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
        {methodDoc}    pub async fn {methodName}(&self, {args}) -> Result<Option<{returnTypeInner}>, ion_rustcore::IonError> {
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
        {methodDoc}    pub async fn {methodName}(&self, {args}) -> Result<ion_rustcore::IonWsStream<{returnType}>, ion_rustcore::IonError> {
                let mut e = ion_rustcore::Encoder::new(Vec::new());
                e.array({argsCount})?;
                {writeArgs}
                let buf = e.into_writer();
                ion_rustcore::IonWsStream::open(&self.ctx, "I{serviceName}", "{originalMethodName}", &buf).await
            }
        """;

    public string ServiceClientMethodDuplexStreamTemplate =>
        """
        {methodDoc}    pub async fn {methodName}(&self, {args}) -> Result<ion_rustcore::IonWsDuplexStream<{inputType}, {returnType}>, ion_rustcore::IonError> {
                let mut e = ion_rustcore::Encoder::new(Vec::new());
                e.array({argsCount})?;
                {writeArgs}
                let buf = e.into_writer();
                ion_rustcore::IonWsDuplexStream::open(&self.ctx, "I{serviceName}", "{originalMethodName}", &buf).await
            }
        """;

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
