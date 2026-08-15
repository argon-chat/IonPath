namespace ion.syntax.test;

using ion.runtime;

/// <summary>
/// End-to-end coverage for inline anonymous types — <c>shipping: msg { … };</c> — the
/// <c>{Owner}{PascalCasedField}</c> hoisting rule, ION0067 (a derived name already taken) and
/// ION0068 (a position with no name to derive from).
/// </summary>
/// <remarks>
/// Hoisting rewrites the tree, so after <c>InlineTypeHoistingStage</c> nothing downstream knows
/// inline types exist. That is the design, and it is also the risk: the one thing every later stage
/// <em>does</em> see is a name the author never wrote, and the placeholder
/// <c>$inline</c> left behind by a body that could not be hoisted. Both are chased here.
/// </remarks>
public class InlineTypeSemanticsTests
{
    // ═══════════════════════════════════════════════════════════════════
    // HOISTING
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The rule: <c>{Owner}{PascalCasedFieldName}</c>, and the field now references it.</summary>
    [Test]
    public void Hoist_AFieldBody_BecomesATopLevelMessage()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg Order { shipping: msg { address: string; postcode: string; }; }
                                               service Api() { Get(): Order; }
                                               """);

        compiled.AssertAccepted();

        Assert.Multiple(() =>
        {
            Assert.That(compiled.FieldType("Order", "shipping"), Is.EqualTo("OrderShipping"));
            Assert.That(compiled.FieldNames("OrderShipping"), Is.EqualTo(new[] { "address", "postcode" }));
            Assert.That(compiled.Lock().Definitions.Keys, Is.EquivalentTo(new[] { "Order", "OrderShipping", "Api" }));
        });
    }

    /// <summary>
    /// Only the first letter of each <c>_</c> separated run is touched, so an already-camel or
    /// already-acronym field name is not mangled.
    /// </summary>
    [TestCase("shipping", "OrderShipping")]
    [TestCase("trace_id", "OrderTraceId")]
    [TestCase("traceID", "OrderTraceID")]
    [TestCase("URL", "OrderURL")]
    [TestCase("a_b_c", "OrderABC")]
    [TestCase("Already", "OrderAlready")]
    public void Hoist_NameDerivation(string field, string derived)
    {
        var compiled = LanguageFeature.Compile($"msg Order {{ {field}: msg {{ z: i4; }}; }}");

        compiled.AssertAccepted();
        Assert.That(compiled.FieldType("Order", field), Is.EqualTo(derived));
    }

    /// <summary>
    /// The owner is the chain from the top level declaration down, so a nested body extends the name
    /// it is written in rather than restarting at the outermost owner.
    /// </summary>
    [Test]
    public void Hoist_NestedBodies_ExtendTheOwnerChain()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg Order { history: msg { at: msg { t: datetime; }; }[]; }
                                               service Api() { Get(): Order; }
                                               """);

        compiled.AssertAccepted();

        Assert.Multiple(() =>
        {
            Assert.That(compiled.FieldType("Order", "history"), Is.EqualTo("Array<OrderHistory>"));
            Assert.That(compiled.FieldType("OrderHistory", "at"), Is.EqualTo("OrderHistoryAt"));
            Assert.That(compiled.FieldNames("OrderHistoryAt"), Is.EqualTo(new[] { "t" }));
        });
    }

    /// <summary>Three levels, to prove the chain is built off the derived name at every step.</summary>
    [Test]
    public void Hoist_ThreeLevelsDeep()
    {
        var compiled = LanguageFeature.Compile(
            "msg A { b: msg { c: msg { d: msg { e: i4; }; }; }; }\nservice Api() { Get(): A; }");

        compiled.AssertAccepted();
        Assert.That(compiled.DefinitionNames, Is.EquivalentTo(new[] { "A", "AB", "ABC", "ABCD" }));
    }

    /// <summary>Every owner kind that yields a name to derive from.</summary>
    [TestCase("msg Order { shipping: msg { z: i4; }; }", "OrderShipping",
        TestName = "Hoist_Owner_MessageField")]
    [TestCase("mixin Audit { stamp: msg { z: i4; }; }\nmsg M with Audit { own: i4; }", "AuditStamp",
        TestName = "Hoist_Owner_MixinField")]
    [TestCase("service Api(ctx: msg { z: i4; }) { Get(): i4; }", "ApiCtx",
        TestName = "Hoist_Owner_ServiceBaseArgument")]
    [TestCase("service Api() { Get(id: msg { z: i4; }): i4; }", "ApiGetId",
        TestName = "Hoist_Owner_MethodArgument")]
    [TestCase("union U { Ok(data: msg { z: i4; }) }", "UOkData",
        TestName = "Hoist_Owner_UnionCaseArgument")]
    [TestCase("union U(stamp: msg { z: i4; }) { Ok(a: i4) }", "UStamp",
        TestName = "Hoist_Owner_UnionSharedField")]
    public void Hoist_OwnerChain(string source, string derived)
    {
        var compiled = LanguageFeature.Compile(source);

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        Assert.That(compiled.DefinitionNames, Does.Contain(derived));
    }

    /// <summary>
    /// An inline type written in a <c>mixin</c> is hoisted once, named after the mixin, rather than
    /// once per message that includes it — which is why hoisting runs before mixin expansion.
    /// </summary>
    [Test]
    public void Hoist_InsideAMixin_HappensOnceForAllIncluders()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin Audit { stamp: msg { at: datetime; }; }
                                               msg M with Audit { own: i4; }
                                               msg N with Audit { other: i4; }
                                               service Api() { Get(): M; Also(): N; }
                                               """);

        compiled.AssertAccepted();

        Assert.Multiple(() =>
        {
            Assert.That(compiled.DefinitionNames, Is.EquivalentTo(new[] { "AuditStamp", "M", "N" }));
            Assert.That(compiled.FieldType("M", "stamp"), Is.EqualTo("AuditStamp"));
            Assert.That(compiled.FieldType("N", "stamp"), Is.EqualTo("AuditStamp"));
            Assert.That(compiled.Lock().Definitions.Keys,
                Is.EquivalentTo(new[] { "AuditStamp", "M", "N", "Api" }));
        });
    }

    /// <summary>Nested inside a mixin, still once, still named after the mixin.</summary>
    [Test]
    public void Hoist_NestedInsideAMixin()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin Audit { stamp: msg { by: msg { id: guid; }; }; }
                                               msg M with Audit { own: i4; }
                                               service Api() { Get(): M; }
                                               """);

        compiled.AssertAccepted();
        Assert.That(compiled.DefinitionNames, Is.EquivalentTo(new[] { "AuditStamp", "AuditStampBy", "M" }));
    }

    /// <summary>
    /// The modifier suffixes apply to the hoisted type exactly as to a named one, including a fixed
    /// size — so <c>msg { … }[4]</c> is an <c>Array&lt;Derived, 4&gt;</c>.
    /// </summary>
    [TestCase("msg { z: i4; }", "MA", TestName = "Hoist_Modifier_None")]
    [TestCase("msg { z: i4; }?", "Maybe<MA>", TestName = "Hoist_Modifier_Optional")]
    [TestCase("msg { z: i4; }[]", "Array<MA>", TestName = "Hoist_Modifier_Array")]
    [TestCase("msg { z: i4; }[4]", "Array<MA, 4>", TestName = "Hoist_Modifier_FixedArray")]
    [TestCase("msg { z: i4; }~", "Partial<MA>", TestName = "Hoist_Modifier_Partial")]
    [TestCase("msg { z: i4; }~[4]?", "Maybe<Array<Partial<MA>, 4>>", TestName = "Hoist_Modifier_All")]
    public void Hoist_ModifiersApplyToTheDerivedType(string written, string canonical)
    {
        var compiled = LanguageFeature.Compile($"msg M {{ a: {written}; }}\nservice Api() {{ Get(): M; }}");

        compiled.AssertAccepted();
        Assert.That(compiled.FieldType("M", "a"), Is.EqualTo(canonical));
    }

    /// <summary>An empty body is a legal, if pointless, message.</summary>
    [Test]
    public void Hoist_AnEmptyBody_IsStillAMessage()
    {
        var compiled = LanguageFeature.Compile("msg Order { shipping: msg { }; }\nservice Api() { Get(): Order; }");

        compiled.AssertAccepted();
        Assert.That(compiled.FieldNames("OrderShipping"), Is.Empty);
    }

    /// <summary>
    /// A doc comment and attributes on the <em>field</em> stay on the field; the hoisted type gets
    /// its own identity and neither of them.
    /// </summary>
    [Test]
    public void Hoist_FieldDocsAndAttributesStayOnTheField()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg Order {
                                                   /// Where it goes.
                                                   @internal
                                                   shipping: msg { address: string; };
                                               }
                                               service Api() { Get(): Order; }
                                               """);

        compiled.AssertAccepted();

        var field = compiled.Definition("Order").fields[0];

        Assert.Multiple(() =>
        {
            Assert.That(field.Doc, Is.EqualTo("Where it goes."));
            Assert.That(field.attributes.Select(a => a.name.Identifier), Does.Contain("internal"));
            Assert.That(compiled.Definition("OrderShipping").Doc, Is.Null);
        });
    }

    /// <summary>A hoisted type is an ordinary declaration: referencing it by name works.</summary>
    [Test]
    public void Hoist_TheDerivedNameIsReferenceable()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg Order { shipping: msg { address: string; }; }
                                               msg Invoice { to: OrderShipping; }
                                               service Api() { Get(): Invoice; }
                                               """);

        compiled.AssertAccepted();
        Assert.That(compiled.FieldType("Invoice", "to"), Is.EqualTo("OrderShipping"));
    }

    /// <summary>
    /// A hoisted type is reachable through the field that produced it, so it is never reported as
    /// dead code.
    /// </summary>
    [Test]
    public void Hoist_ADerivedTypeIsNotUnused()
    {
        var compiled = LanguageFeature.Compile(
            "msg Order { shipping: msg { z: i4; }; }\nservice Api() { Get(): Order; }");

        compiled.AssertAccepted();
        Assert.That(compiled.WithCode(LanguageFeature.Advisory), Is.Empty, compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ION0067 — the derived name is taken
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every declaration kind the derived name can collide with. A hard error, never a silent
    /// rename: a rename would change a name in <c>ion.lock.json</c> and three generated languages
    /// that nobody asked to change.
    /// </summary>
    [TestCase("msg OrderS { z: i4; }", "msg 'OrderS'", TestName = "Collision_WithAMessage")]
    [TestCase("enum OrderS { A }", "enum 'OrderS'", TestName = "Collision_WithAnEnum")]
    [TestCase("flags OrderS : u4 { A = 1 }", "flags 'OrderS'", TestName = "Collision_WithFlags")]
    [TestCase("union OrderS { Ok(a: i4) }", "union 'OrderS'", TestName = "Collision_WithAUnion")]
    [TestCase("typedef OrderS = i4;", "typedef 'OrderS'", TestName = "Collision_WithATypedef")]
    [TestCase("service OrderS() { Go(): i4; }", "service 'OrderS'", TestName = "Collision_WithAService")]
    [TestCase("attribute @OrderS(v: i4);", "attribute '@OrderS'", TestName = "Collision_WithAnAttribute")]
    [TestCase("mixin OrderS { q: i4; }", "mixin 'OrderS'", TestName = "Collision_WithAMixin")]
    public void Collision_WithADeclaration_IsReported(string declaration, string holder)
    {
        var compiled = LanguageFeature.Compile($"{declaration}\nmsg Order {{ s: msg {{ a: i4; }}; }}");

        Assert.That(compiled.Only(LanguageFeature.InlineNameCollision).Message,
            Is.EqualTo($"The inline type on the field 's' of msg 'Order' hoists to 'OrderS', but " +
                       $"{holder} already has that name. Rename the field, or declare the type " +
                       "explicitly and reference it by name."));
    }

    /// <summary>A builtin owns its name too, and is named as one.</summary>
    [Test]
    public void Collision_WithABuiltin_IsReported()
    {
        var compiled = LanguageFeature.Compile("msg Order { s: msg { a: i4; }; }\nmsg OrderS { z: i4; }");

        Assert.That(compiled.Only(LanguageFeature.InlineNameCollision).Message,
            Does.Contain("msg 'OrderS' already has that name"),
            "the holder is found whether it is declared before or after the inline type");
    }

    /// <summary>
    /// Two inline types deriving the same name. Reported as the pair of fields, which is what tells
    /// the reader which two lines pascal-case to the same thing — not as a collision with a
    /// declaration they cannot find in their file.
    /// </summary>
    [Test]
    public void Collision_BetweenTwoInlineTypes_NamesBothFields()
    {
        var compiled = LanguageFeature.Compile("msg Order { trace_id: msg { a: i4; }; traceId: msg { b: i4; }; }");

        Assert.That(compiled.Only(LanguageFeature.InlineNameCollision).Message,
            Is.EqualTo("The inline types on the field 'trace_id' of msg 'Order' and on the field " +
                       "'traceId' of msg 'Order' both hoist to 'OrderTraceId'. Rename one of the fields."));
    }

    /// <summary>A leading underscore is dropped by the pascal-casing, so <c>_x</c> and <c>x</c> collide.</summary>
    [Test]
    public void Collision_LeadingUnderscore_CollidesWithThePlainName()
        => Assert.That(LanguageFeature.Compile("msg Order { _x: msg { a: i4; }; x: msg { b: i4; }; }")
            .Only(LanguageFeature.InlineNameCollision).Message, Does.Contain("both hoist to 'OrderX'"));

    /// <summary>
    /// Case matters: <c>ResolveTypeFor</c> compares with <c>string.Equals</c>, so a derived name
    /// differing only in case is not a resolution hazard and is left to ION0002.
    /// </summary>
    [Test]
    public void Collision_DifferingOnlyInCase_IsNotAnInlineCollision()
    {
        var compiled = LanguageFeature.Compile("msg orders { z: i4; }\nmsg Order { s: msg { a: i4; }; }");

        Assert.That(compiled.WithCode(LanguageFeature.InlineNameCollision), Is.Empty, compiled.Describe);
    }

    /// <summary>ION0067 squiggles the inline body, which is the thing that has to change.</summary>
    [Test]
    public void Collision_Position_PointsAtTheInlineBody()
    {
        //             1         2         3
        //    123456789012345678901234567890123456789
        //    msg Order { shipping: msg { a: i4; }; }     (line 2)
        var compiled = LanguageFeature.Compile(
            "msg OrderShipping { z: i4; }\nmsg Order { shipping: msg { a: i4; }; }");

        LanguageFeature.AssertSpan(compiled.Only(LanguageFeature.InlineNameCollision), 2, 23, 37);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ION0068 — nowhere to derive a name from
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every position the grammar accepts <c>msg { … }</c> in but the naming rule cannot serve.
    /// Rejected rather than named by invention: a type called <c>Result</c> the author never wrote is
    /// a name they then own forever, because it is in the lock.
    /// </summary>
    [TestCase("msg M { a: Array<msg { z: i4; }>; }", "a generic argument", 1, 18, 32,
        TestName = "NotAllowed_GenericArgument")]
    [TestCase("msg M { a: Map<string, msg { z: i4; }>; }", "a generic argument", 1, 24, 38,
        TestName = "NotAllowed_MapValueArgument")]
    [TestCase("msg M { a: Map<msg { z: i4; }, i4>; }", "a generic argument", 1, 16, 30,
        TestName = "NotAllowed_MapKeyArgument")]
    [TestCase("typedef T = msg { z: i4; };", "the underlying type of a typedef", 1, 13, 27,
        TestName = "NotAllowed_TypedefUnderlying")]
    [TestCase("typedef msg { z: i4; } = i4;", "the name of a typedef", 1, 9, 23,
        TestName = "NotAllowed_TypedefName")]
    [TestCase("enum E : msg { z: i4; } { A }", "the base type of an enum", 1, 10, 24,
        TestName = "NotAllowed_EnumBase")]
    [TestCase("flags F : msg { z: i4; } { A = 1 }", "the base type of a flags declaration", 1, 11, 25,
        TestName = "NotAllowed_FlagsBase")]
    [TestCase("attribute @x(v: msg { z: i4; });", "the type of an attribute parameter", 1, 17, 31,
        TestName = "NotAllowed_AttributeParameter")]
    [TestCase("service S() { Get(): msg { z: i4; }; }", "the return type of a method", 1, 22, 36,
        TestName = "NotAllowed_MethodReturnType")]
    [TestCase("union U { msg { z: i4; } }", "a union case", 1, 11, 25,
        TestName = "NotAllowed_UnionCaseName")]
    public void NotAllowed_IsReportedWithItsPosition(string source, string position,
        int line, int startCol, int endCol)
    {
        var compiled = LanguageFeature.Compile(source);
        var diagnostic = compiled.Only(LanguageFeature.InlineNotAllowed);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.HasParseErrors, Is.False, "the fixture must actually parse");
            Assert.That(diagnostic.Severity, Is.EqualTo(IonDiagnosticSeverity.Error));
            Assert.That(diagnostic.Message, Does.StartWith(
                $"An inline 'msg {{ … }}' cannot be written as {position}: there is no field name " +
                "to derive a type name from."));
            LanguageFeature.AssertSpan(diagnostic, line, startCol, endCol);
        });
    }

    /// <summary>
    /// A method's return type is the one rejected position that has a perfectly good owner and still
    /// no name to derive from — the method name alone would give <c>ApiGet</c>, which is a name for
    /// the call rather than for the payload.
    /// </summary>
    [Test]
    public void NotAllowed_MethodReturnType_IsRejectedEvenThoughAnOwnerExists()
    {
        var compiled = LanguageFeature.Compile("service Api() { Get(id: msg { z: i4; }): msg { q: i4; }; }");

        Assert.Multiple(() =>
        {
            // The argument beside it hoists perfectly well…
            Assert.That(compiled.DefinitionNames, Does.Contain("ApiGetId"));
            // …while the return type is refused, and refused exactly once.
            Assert.That(compiled.WithCode(LanguageFeature.InlineNotAllowed), Has.Count.EqualTo(1),
                compiled.Describe);
            Assert.That(compiled.Only(LanguageFeature.InlineNotAllowed).Message,
                Does.Contain("the return type of a method"));
            Assert.That(compiled.DefinitionNames, Has.No.Member("ApiGet"));
        });
    }

    /// <summary>Each rejected position states its own remedy.</summary>
    [TestCase("msg M { a: Array<msg { z: i4; }>; }", "write that name as the argument")]
    [TestCase("typedef T = msg { z: i4; };", "an alias for an anonymous type has nothing to alias")]
    [TestCase("enum E : msg { z: i4; } { A }", "must be an integral builtin")]
    [TestCase("service S() { Get(): msg { z: i4; }; }", "return that name")]
    [TestCase("union U { msg { z: i4; } }", "write 'case <Name>'")]
    public void NotAllowed_StatesTheRemedy(string source, string remedy)
        => Assert.That(LanguageFeature.Compile(source).Only(LanguageFeature.InlineNotAllowed).Message,
            Does.Contain(remedy));

    /// <summary>
    /// A rejected position at depth is still rejected, and the body inside a rejected body is reached
    /// too.
    /// </summary>
    [Test]
    public void NotAllowed_AtDepth_IsStillReported()
    {
        var compiled = LanguageFeature.Compile("msg M { a: Map<string, Array<Set<msg { z: i4; }>>>; }");

        Assert.That(compiled.WithCode(LanguageFeature.InlineNotAllowed), Has.Count.EqualTo(1),
            compiled.Describe);
    }

    /// <summary>
    /// Rejecting the outer position must not stop the compiler noticing an inner one: a generic
    /// argument written inside an otherwise hoistable body is its own mistake.
    /// </summary>
    [Test]
    public void NotAllowed_InsideAHoistedBody_IsStillReported()
    {
        var compiled = LanguageFeature.Compile("msg Order { s: msg { inner: Array<msg { z: i4; }>; }; }");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(LanguageFeature.InlineNotAllowed), Has.Count.EqualTo(1),
                compiled.Describe);
            Assert.That(compiled.DefinitionNames, Does.Contain("OrderS"));
        });
    }

    /// <summary>
    /// BUG — an inline body written as a union case name that carries an argument list is neither
    /// hoisted nor rejected. <c>InlineTypeHoistingStage.HoistFile</c> only calls <c>RejectInline</c>
    /// when <c>@case.IsTypeRef</c>; the other branch calls <c>RejectInlineInArguments</c>, which
    /// inspects the case name's <em>generic arguments</em> and never its own <c>InlineBody</c>. The
    /// compile succeeds, and the placeholder reaches the IR and <c>ion.lock.json</c> as a union case
    /// literally named <c>$inline</c> — which three generators would then emit.
    /// </summary>
    [Test]
    public void NotAllowed_UnionCaseNameWithArguments_IsRejected()
    {
        var compiled = LanguageFeature.Compile("union U { msg { z: i4; }(a: i4) }\nmsg M { u: U; }\n" +
                                               "service Api() { Get(): M; }");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.HasParseErrors, Is.False);
            Assert.That(compiled.WithCode(LanguageFeature.InlineNotAllowed), Has.Count.EqualTo(1),
                compiled.Describe);
            Assert.That(compiled.Lock().Definitions["U"].Cases!.Select(c => c.Name),
                Has.No.Member(LanguageFeature.InlinePlaceholder),
                "the unlexable placeholder must never reach the schema lock");
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // NO CASCADES, AND NO LEAKED NAMES
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>$inline</c> is deliberately unlexable so that a consumer which forgets to branch on
    /// <c>InlineBody</c> fails loudly. It must never be quoted at a human: no author has ever typed
    /// it, so a message containing it names nothing the reader can find.
    /// </summary>
    [TestCaseSource(nameof(EveryRejectedInlinePosition))]
    public void NoLeak_ThePlaceholderNeverAppearsInADiagnostic(string source)
    {
        var compiled = LanguageFeature.Compile(source);

        Assert.That(compiled.Diagnostics.Where(d => d.Message.Contains(LanguageFeature.InlinePlaceholder)),
            Is.Empty, compiled.Describe);
    }

    private static IEnumerable<TestCaseData> EveryRejectedInlinePosition() => new[]
    {
        "msg M { a: Array<msg { z: i4; }>; }",
        "msg M { a: Map<string, msg { z: i4; }>; }",
        "msg M { a: Map<msg { z: i4; }, i4>; }",
        "typedef T = msg { z: i4; };\nmsg M { a: T; }",
        "enum E : msg { z: i4; } { A }",
        "flags F : msg { z: i4; } { A = 1 }",
        "attribute @x(v: msg { z: i4; });",
        "service S() { Get(): msg { z: i4; }; }",
        "union U { msg { z: i4; } }",
        "union U { msg { z: i4; }(a: i4) }"
    }.Select((s, i) => new TestCaseData(s).SetName($"NoLeak_{i:00}"));

    /// <summary>
    /// An un-hoistable body is one mistake. ION0068 owns it, and neither the unresolved-name check
    /// nor the arity check may stack on the <c>$inline</c> placeholder it leaves behind.
    /// </summary>
    [TestCase("msg M { a: Array<msg { z: i4; }>; }", TestName = "NoCascade_GenericArgument")]
    [TestCase("typedef T = msg { z: i4; };\nmsg M { a: T; }", TestName = "NoCascade_ThroughATypedef")]
    [TestCase("service S() { Get(): msg { z: i4; }; }", TestName = "NoCascade_MethodReturnType")]
    public void NoCascade_OnlyION0068_IsReported(string source)
    {
        var compiled = LanguageFeature.Compile(source);

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.InlineNotAllowed }),
            compiled.Describe);
    }

    /// <summary>
    /// BUG — an inline Map key draws both ION0068 (a generic argument is not a hoistable position)
    /// and ION0061 (an inline type cannot be a key), on the same span, for one mistake.
    /// <c>GenericTypeValidationStage.ValidateArity</c> deliberately returns early on
    /// <c>site.IsInline</c> for exactly this reason; <c>ValidateMapKey</c> does not do the same for
    /// the key, and <c>DescribeKey</c> carries an inline arm that can only ever fire after ION0068
    /// already has.
    /// </summary>
    [Test]
    public void NoCascade_AnInlineMapKey_IsOneDiagnostic()
    {
        var compiled = LanguageFeature.Compile("msg M { m: Map<msg { z: i4; }, i4>; }");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.InlineNotAllowed }),
            compiled.Describe);
    }

    /// <summary>
    /// BUG — when the derived name is held by something that is not a type, ION0067 is followed by a
    /// second error about a name the author never wrote at a span they wrote as <c>msg { … }</c>.
    /// <c>Claim</c> returns <see langword="false"/> and the field is still rewritten to the derived
    /// name "so the mistake stays one diagnostic", which only holds when the holder happens to be a
    /// type: a <c>service</c> or an <c>attribute</c> adds ION0009, a <c>mixin</c> adds ION0066.
    /// </summary>
    [TestCase("service OrderS() { Go(): i4; }", TestName = "NoCascade_CollisionWithAService")]
    [TestCase("attribute @OrderS(v: i4);", TestName = "NoCascade_CollisionWithAnAttribute")]
    [TestCase("mixin OrderS { q: i4; }", TestName = "NoCascade_CollisionWithAMixin")]
    public void NoCascade_ACollisionWithANonType_IsOneDiagnostic(string declaration)
    {
        var compiled = LanguageFeature.Compile($"{declaration}\nmsg Order {{ s: msg {{ a: i4; }}; }}");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.InlineNameCollision }),
            compiled.Describe);
    }

    /// <summary>
    /// BUG — the worst reading of the same rewrite. When the derived name happens to be the
    /// <em>including</em> message, ION0067 is followed by an ION0030 claiming
    /// <c>AS → AS</c>: the rewrite made the message own itself. The cycle is entirely an artefact of
    /// the compiler's own substitution, and the remedy it offers ("make one of those fields optional")
    /// is advice about a field the author did not write.
    /// </summary>
    [Test]
    public void NoCascade_ACollisionWithTheIncludingMessage_DoesNotInventACycle()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { s: msg { q: i4; }; }
                                               msg AS with A { y: i4; }
                                               """);

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.InlineNameCollision }),
            compiled.Describe);
    }

    /// <summary>
    /// BUG — a diagnostic about an inline type quotes the compiler's derived name instead of what the
    /// author wrote. <c>IonTypeSites.NameAsWritten</c> promises "an inline body renders as
    /// <c>msg { … }</c>", but <c>TypeModifierValidationStage</c> runs <em>after</em>
    /// <c>InlineTypeHoistingStage</c> has replaced the body with the derived name, so the promise only
    /// holds for a body that was refused. The rendering is therefore inconsistent between the two
    /// halves of the same feature: <c>Array&lt;msg { … }[0]&gt;</c> echoes <c>msg { … }[0]</c>, while
    /// the hoistable <c>msg { … }[0]</c> beside it echoes <c>MM[0]</c>.
    /// </summary>
    [TestCase("msg M { m: msg { z: i4; }?~; }", "ION0010", TestName = "NoLeak_DerivedName_InION0010")]
    [TestCase("msg M { m: msg { z: i4; }[0]; }", LanguageFeature.FixedArraySize,
        TestName = "NoLeak_DerivedName_InION0062")]
    [TestCase("msg M { m: msg { z: i4; }[][]; }", "ION0019", TestName = "NoLeak_DerivedName_InION0019")]
    public void NoLeak_ADiagnosticQuotesWhatTheAuthorWrote(string source, string code)
    {
        var compiled = LanguageFeature.Compile(source);

        Assert.That(compiled.Only(code).Message, Does.Contain("msg { … }"),
            "an inline type must be echoed as written, not as the name the compiler derived for it");
    }

    // ═══════════════════════════════════════════════════════════════════
    // THE LOCK
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A hoisted type is an ordinary lock entry under its derived name — which is precisely why
    /// ION0067 has to be a hard error rather than a rename.
    /// </summary>
    [Test]
    public void Lock_ContainsTheHoistedTypeUnderItsDerivedName()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg Order { shipping: msg { address: string; }; }
                                               service Api() { Get(): Order; }
                                               """);

        compiled.AssertAccepted();

        var locked = compiled.Lock();

        Assert.Multiple(() =>
        {
            Assert.That(locked.Definitions["Order"].Fields![0].Type, Is.EqualTo("OrderShipping"));
            Assert.That(locked.Definitions["OrderShipping"].Fields!.Select(f => f.Name),
                Is.EqualTo(new[] { "address" }));
        });
    }

    /// <summary>
    /// Renaming the field renames the type, which is a breaking change to two entries at once. Worth
    /// pinning: it is the cost of the naming rule, and an author has to be able to see it.
    /// </summary>
    [Test]
    public void Lock_RenamingTheFieldRenamesTheType()
    {
        var before = LanguageFeature.Compile("""
                                             msg Order { shipping: msg { address: string; }; }
                                             service Api() { Get(): Order; }
                                             """);
        before.AssertAccepted();

        var after = LanguageFeature.Compile("""
                                            msg Order { delivery: msg { address: string; }; }
                                            service Api() { Get(): Order; }
                                            """, before.Lock());

        Assert.That(after.Diagnostics.Where(d => d.Code.StartsWith("ION002", StringComparison.Ordinal)),
            Is.Not.Empty, "the derived type name is part of the wire contract");
    }

    // ═══════════════════════════════════════════════════════════════════
    // SCALE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deeply nested bodies hoist innermost-first without recursing off the stack, and every level
    /// gets its own declaration.
    /// </summary>
    [Test]
    public void Scale_DeepNesting_HoistsEveryLevel()
    {
        const int depth = 20;

        var body = "i4";
        for (var i = 0; i < depth; i++)
            body = $"msg {{ b: {body}; }}";

        var compiled = ParseBudget.Within(() => LanguageFeature.Compile($"msg M {{ a: {body}; }}"));

        Assert.That(compiled.HasParseErrors, Is.False, "the fixture must parse to prove anything");
        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        // M, MA, MAB, MABB, … — one derived type per level.
        Assert.That(compiled.DefinitionNames, Has.Count.EqualTo(depth + 1));
    }

    /// <summary>
    /// Past the grammar's nesting budget the parse fails fast with an ordinary error rather than
    /// overflowing the stack, which is not catchable on .NET and would take the test host with it.
    /// </summary>
    [Test]
    public void Scale_PathologicalNesting_FailsFast()
    {
        var body = "i4";
        for (var i = 0; i < 5000; i++)
            body = $"msg {{ b: {body}; }}";

        var file = ParseBudget.Within(() => IonParser.Parse("deep", $"msg M {{ a: {body}; }}"));

        Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Not.Empty);
        Assert.That(file.messageSyntaxes, Is.Empty);
    }
}
