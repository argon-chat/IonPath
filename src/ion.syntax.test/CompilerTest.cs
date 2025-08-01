﻿namespace ion.syntax.test;

using ion.compiler;

public class CompilerTest
{
    [Test]
    public void TestDfs()
    {
        var ctx = new CompilationContext();

        new ImportCycleDetectionStage(ctx).Run([
            new IonFileSyntax("a1", new FileInfo("a1"), [new IonUseSyntax("a2")], [], [], [], [], [], []),
            new IonFileSyntax("a2", new FileInfo("a2"), [new IonUseSyntax("a1")], [], [], [], [], [], [])
        ]);

        new ImportCycleDetectionStage(ctx).Run([
            new IonFileSyntax("a1", new FileInfo("a1"), [new IonUseSyntax("a2")], [], [], [], [], [], []),
            new IonFileSyntax("a2", new FileInfo("a2"), [new IonUseSyntax("a3")], [], [], [], [], [], []),
            new IonFileSyntax("a3", new FileInfo("a3"), [new IonUseSyntax("a1")], [], [], [], [], [], []),
        ]);
    }
}