# Lavender Code Intelligence: From Source Files to Structured Knowledge

This document explains Lavender's indexing and code-intelligence functionality from the ground up. It has two goals:

1. Give you a broad mental model of the new logic and how the pieces cooperate.
2. Walk through each component step by step, including the C# syntax and Roslyn APIs used by the implementation.

The explanations refer to the code currently in `src/`. The guide is intended to be read while you have the corresponding files open.

---

## Part I: The broad explanation

### 1. What Lavender is building

Lavender turns a C# solution into several kinds of searchable knowledge:

- **Chunks** answer fuzzy questions such as “where is project searching implemented?”
- **Symbols** identify exact program elements such as one particular overloaded method.
- **Relationships** answer structural questions such as “who calls this method?” or “what implements this interface?”
- **Source retrieval** returns the authoritative declaration for an exact symbol.
- **Diagnostics** expose compiler errors and warnings as structured records.
- **Dependencies** describe which projects, packages, and assemblies depend on one another.
- **Git context** describes uncommitted work and recent repository history.

These are different views of the same project. They are kept separate because they solve different problems.

For example, vector search is good at finding code that is conceptually related to a natural-language question, but it is not an authoritative way to answer “give me the exact body of this method.” Exact source retrieval is good at that second question because it starts from a precise Roslyn symbol.

### 2. The complete indexing flow

When the user selects a folder, the flow is:

```text
MainWindow.OpenProject_Click
    |
    | Find a .sln or a single .csproj
    v
ProjectIndexer.IndexProjectAsync
    |
    +--> CodeChunkService
    |       Parse files as syntax
    |       Create embedding-oriented chunks
    |
    +--> IndexedProjectContext.OpenAsync
    |       Open one MSBuildWorkspace
    |       Load the Roslyn Solution
    |
    +--> SymbolIndexingService
    |       Build exact symbol metadata
    |       Keep a symbol-ID-to-ISymbol lookup
    |
    +--> CodeRelationshipIndexer
    |       Resolve inheritance, interfaces, calls, and type use
    |
    +--> ProjectDependencyIndexer
    |       Build solution/project/package/assembly edges
    |
    +--> ProjectKnowledgeService
    |       Compose all read-only query services
    |
    +--> FastApiService.EmbedProjectAsync
            Send only chunks to the Python vector service
```

Source retrieval, diagnostics, and Git commands are mostly **on demand**. They do not all run every time semantic indexing examines a document.

### 3. Why syntax and semantics are different

Roslyn exposes two major ways to understand C#:

#### Syntax

Syntax represents what was written.

Given:

```csharp
Helper();
```

The syntax tree can tell us:

- This is an invocation expression.
- The written name is `Helper`.
- It is at a particular character span and line.

Syntax alone cannot reliably tell us which `Helper` method was selected. There could be overloads, inherited members, extension methods, or a method in another type.

#### Semantics

Semantics represents what the compiler resolved the code to mean.

With a `SemanticModel`, Roslyn can tell us:

- The invocation resolves to a particular `IMethodSymbol`.
- The selected overload accepts certain parameter types.
- The method belongs to a particular containing type and assembly.

Lavender uses syntax-only parsing for chunks because chunk extraction mostly needs source boundaries and names. It uses semantic models for symbols and relationships because those features need compiler-level identity.

### 4. Why one shared Roslyn context matters

Creating an `MSBuildWorkspace` and loading a solution is expensive. Asking every service to reopen the solution would:

- Repeat MSBuild evaluation.
- Repeat project loading.
- Create unrelated Roslyn object graphs.
- Make symbol identity and source lookup harder to coordinate.
- Waste memory and time.

`IndexedProjectContext` owns one workspace and one loaded solution. Symbol indexing, relationships, diagnostics, dependencies, and source retrieval all work from that shared context.

### 5. The kinds of identity in Lavender

Lavender has several identifiers:

- A **chunk ID** identifies an embedding unit.
- A **symbol ID** identifies a C# declaration.
- A **project dependency node ID** identifies a solution, project, package, or assembly.

These IDs should not be mixed.

A method symbol ID includes its semantic signature, so:

```csharp
Search(string query)
Search(string query, int topK)
```

produce different identities.

The relationship graph uses the same symbol IDs as the symbol index. This is what makes a workflow like the following possible:

```text
Find a symbol
    -> get its ID
    -> retrieve its exact source
    -> find callers using the same ID
    -> map diagnostics back to the same ID
```

---

## Part II: C# and Roslyn concepts used by the code

### 6. Important C# syntax in these services

#### File-scoped namespaces

```csharp
namespace Lavender.Infrastructure.Indexing;
```

This means every declaration in the file belongs to that namespace. It is the compact equivalent of wrapping the file in:

```csharp
namespace Lavender.Infrastructure.Indexing
{
    // declarations
}
```

#### `sealed`

```csharp
public sealed class SymbolIndex
```

`sealed` means another class cannot inherit from this class. These services use it where inheritance is not part of the design.

#### Nullable reference types

```csharp
Compilation? compilation
```

The `?` means the value may be `null`. Roslyn's `GetCompilationAsync` can return `null`, so the code must check it:

```csharp
if (compilation is null) continue;
```

#### `init` properties

```csharp
public string SymbolId { get; init; } = "";
```

An `init` property can be assigned while constructing an object:

```csharp
var result = new SymbolSourceResult
{
    SymbolId = id
};
```

After initialization, normal callers cannot change it. This is useful for result models.

#### Expression-bodied methods

```csharp
public CodeSymbol? Get(string id) => _symbols.GetValueOrDefault(id);
```

This is a short form of:

```csharp
public CodeSymbol? Get(string id)
{
    return _symbols.GetValueOrDefault(id);
}
```

#### Pattern matching

```csharp
if (compilation is null) continue;
```

and:

```csharp
symbol is IMethodSymbol { ReducedFrom: not null } method
```

The second expression checks that:

- `symbol` implements `IMethodSymbol`.
- Its `ReducedFrom` property is not null.
- The matched method is stored in the local variable `method`.

#### Switch expressions

```csharp
return node switch
{
    BaseMethodDeclarationSyntax n => model.GetDeclaredSymbol(n, token),
    BasePropertyDeclarationSyntax n => model.GetDeclaredSymbol(n, token),
    _ => null
};
```

This selects a result based on the runtime type of `node`. `_` is the fallback case.

#### Tuples

```csharp
var entries = new List<(CodeSymbol, ISymbol)>();
```

Each list entry holds two related values: Lavender's serializable model and Roslyn's live symbol.

Named tuple elements make their purpose clearer:

```csharp
IEnumerable<(CodeSymbol Model, ISymbol Symbol)> entries
```

#### Generic collections

```csharp
Dictionary<string, ISymbol>
HashSet<string>
IReadOnlyList<CodeRelationship>
```

- A `Dictionary<TKey, TValue>` performs key-based lookup.
- A `HashSet<T>` efficiently tests whether a value exists and prevents duplicates.
- `IReadOnlyList<T>` lets callers read results without exposing mutation methods.

#### LINQ

```csharp
root.DescendantNodes()
    .OfType<MethodDeclarationSyntax>()
```

LINQ builds data-processing pipelines:

- `DescendantNodes()` produces syntax nodes.
- `OfType<T>()` retains nodes of a specific type.
- `Where(...)` filters.
- `Select(...)` transforms.
- `GroupBy(...)` groups values.
- `ToArray()` executes the query and creates an array.

#### `async` and `await`

```csharp
Compilation? compilation =
    await project.GetCompilationAsync(cancellationToken);
```

`GetCompilationAsync` returns a `Task<Compilation?>`. `await` asynchronously waits for the result without blocking the UI thread.

The containing method must be marked `async` and normally returns `Task` or `Task<T>`.

#### Cancellation tokens

```csharp
CancellationToken cancellationToken = default
```

A caller may request cancellation. The token is passed into Roslyn and process APIs, and explicit checks use:

```csharp
cancellationToken.ThrowIfCancellationRequested();
```

Cancellation is not treated as a normal indexing error; it propagates to the caller.

#### `IDisposable` and `using`

Workspaces and processes hold operating-system or compiler resources. `IDisposable` provides a deterministic cleanup method:

```csharp
public void Dispose()
{
    _workspace.Dispose();
}
```

For a local value, `using` automatically calls `Dispose`:

```csharp
using var process = new Process();
```

### 7. Roslyn's object model

The most important hierarchy is:

```text
MSBuildWorkspace
    -> Solution
        -> Project
            -> Document
                -> SyntaxTree
                    -> SyntaxNode

Project
    -> Compilation
        -> SemanticModel for a SyntaxTree
            -> ISymbol and ITypeSymbol results
```

#### `MSBuildWorkspace`

Loads `.sln` and `.csproj` files using MSBuild rules. It understands project references, source documents, compilation options, and metadata references.

#### `Solution`

An immutable snapshot of all loaded projects.

#### `Project`

A Roslyn view of one C# project. It exposes documents, references, and `GetCompilationAsync`.

#### `Document`

A source document in a project. It can asynchronously provide its syntax root and syntax tree.

#### `SyntaxTree`

The parsed tree for one source file. It maps source text to structured nodes and preserves character spans.

#### `SyntaxNode`

A node such as:

- `ClassDeclarationSyntax`
- `MethodDeclarationSyntax`
- `InvocationExpressionSyntax`
- `ObjectCreationExpressionSyntax`
- `TypeSyntax`

#### `Compilation`

The compiler's complete view of a project: syntax trees, references, language options, and symbols.

#### `SemanticModel`

Connects one syntax tree to a compilation. It answers questions such as:

```csharp
model.GetDeclaredSymbol(declaration)
model.GetSymbolInfo(expression)
model.GetTypeInfo(typeSyntax)
model.GetEnclosingSymbol(position)
```

#### `ISymbol`

The common abstraction for namespaces, types, methods, fields, properties, events, and parameters.

Specialized interfaces include:

- `INamedTypeSymbol`
- `IMethodSymbol`
- `IPropertySymbol`
- `IFieldSymbol`
- `IEventSymbol`

---

## Part III: Step-by-step component guide

## 8. UI project selection

File: `src/Lavender.App/MainWindow.xaml.cs`

### Purpose

The UI chooses the folder that becomes Lavender's indexing scope.

### Steps

1. `OpenFolderDialog` asks the user for a directory.
2. `FindSolution` searches the directory for a top-level `.sln`.
3. If there is no solution, it accepts exactly one top-level `.csproj`.
4. If discovery fails, the method displays a warning and returns.
5. The folder tree and search services are initialized.
6. `ProjectIndexer.IndexProjectAsync` starts indexing.
7. Any indexing exception is displayed instead of escaping the WPF `async void` event handler.

The early `return` after discovery failure is important. Without it, an empty path reaches `Path.GetFullPath`, which causes “The path is empty.”

---

## 9. ProjectIndexer: the coordinator

File: `src/Lavender.Infrastructure/Indexing/ProjectIndexer.cs`

### Purpose

`ProjectIndexer` is the orchestration point. It does not implement parsing itself; it calls the specialized services in the required order.

### Step 1: validate inputs

```csharp
ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
```

These guard clauses fail immediately with a clear error when a path is missing.

It then distinguishes:

- `projectPath`: the selected directory used for file scanning and Git.
- `solutionPath`: the `.sln` or `.csproj` loaded through MSBuild.

### Step 2: build chunks

```csharp
List<CodeChunk> chunks =
    CodeChunkService.GetCodeChunksFromFolder(projectPath);
```

Chunking is performed from files on disk. It does not need a compilation.

### Step 3: open the shared context

```csharp
IndexedProjectContext newContext =
    await IndexedProjectContext.OpenAsync(solutionPath, cancellationToken);
```

Every semantic service receives this same context.

### Step 4: build canonical symbol identity and index

```csharp
var identity = new SymbolIdentityService();
SymbolIndex symbols =
    await new SymbolIndexingService(identity)
        .IndexAsync(newContext, cancellationToken);
```

One identity service is shared by symbol, relationship, and diagnostic code.

### Step 5: build graphs

```csharp
CodeRelationshipGraph relationships =
    await new CodeRelationshipIndexer(identity)
        .IndexAsync(newContext, symbols, cancellationToken);

ProjectDependencyGraph dependencies =
    new ProjectDependencyIndexer().Index(newContext);
```

The relationship graph requires semantic models and the symbol index. The dependency graph mostly reads solution/project metadata.

### Step 6: construct the facade

`ProjectKnowledgeService` receives the finished indexes and on-demand providers.

### Step 7: replace the previous context

```csharp
IndexedProjectContext? old = _context;
_context = newContext;
old?.Dispose();
```

The null-conditional operator `?.` means “call `Dispose` only when `old` is not null.”

The old context is disposed only after the new one is successfully built.

### Step 8: embed chunks

```csharp
await FastApiService.Instance.EmbedProjectAsync(chunks);
```

Only chunks go to the vector backend. Symbols, diagnostics, and graphs remain C# structured data.

---

## 10. IndexedProjectContext: one Roslyn workspace

File: `src/Lavender.Infrastructure/Indexing/IndexedProjectContext.cs`

### Purpose

This class owns the lifetime of:

- `MSBuildWorkspace`
- Workspace warning registration
- Loaded `Solution`
- Solution/project input path
- Root directory

### Opening a solution

```csharp
if (!MSBuildLocator.IsRegistered)
    MSBuildLocator.RegisterDefaults();
```

`MSBuildLocator` tells Roslyn which installed MSBuild toolset to use. Registration must happen before creating MSBuild-backed Roslyn objects.

```csharp
var workspace = MSBuildWorkspace.Create();
```

This creates the loader.

```csharp
workspace.RegisterWorkspaceFailedHandler(...)
```

Workspace warnings are collected. MSBuildWorkspace can partially load projects while reporting warnings, so warnings are useful without necessarily being fatal.

### Solution versus project

```csharp
Solution solution =
    Path.GetExtension(fullPath) == ".sln"
        ? await workspace.OpenSolutionAsync(...)
        : (await workspace.OpenProjectAsync(...)).Solution;
```

The conditional operator has the form:

```csharp
condition ? valueWhenTrue : valueWhenFalse
```

Opening one project still produces a `Solution` snapshot containing that project.

### Resource ownership

If opening fails, the registration and workspace are immediately disposed. If it succeeds, the context retains them until `Dispose`.

This is why callers must keep the context alive while using Roslyn symbols and documents.

---

## 11. Code chunk indexing

Files:

- `src/Lavender.Core/DataTypes/CodeChunk.cs`
- `src/Lavender.Infrastructure/Indexing/Chunking/CodeChunkService.cs`

### Purpose

Chunks are text units designed for embedding and semantic search. They answer approximate, natural-language queries.

### Step 1: scan C# files

`GetCodeChunksFromFolder` obtains C# file paths and calls the per-file parser.

### Step 2: parse source

```csharp
SyntaxTree tree = CSharpSyntaxTree.ParseText(fileText);
SyntaxNode root = tree.GetRoot();
```

`ParseText` needs only source text. It does not know the project's references or compiler binding.

### Step 3: create a whole-file chunk

The whole-file chunk stores the complete source and file metadata.

### Step 4: traverse declarations

Typical code:

```csharp
root.DescendantNodes()
    .OfType<ClassDeclarationSyntax>()
```

This walks the syntax tree and keeps only class declarations.

The service creates chunks for:

- File summaries
- Class fields
- Methods
- Constructors
- Properties
- Records
- Structs
- Interfaces
- Enums

### Step 5: calculate source ranges

```csharp
var lineSpan = methodNode.SyntaxTree.GetLineSpan(methodNode.Span);
int startLine = lineSpan.StartLinePosition.Line + 1;
```

Roslyn lines are zero-based. Lavender adds one for human-readable line numbers.

`Span` is a character range in the source text. `GetLineSpan` translates it into line/column positions.

### Step 6: build IDs

Chunk IDs are SHA-256 hashes of values such as file path, declaration name, type, or line. These are chunk identities, not semantic symbol identities.

### Step 7: build embedding text

`BuildEmbeddingText` combines metadata and source:

```text
File: ...
Namespace: ...
Chunk Type: ...
Class: ...
Member: ...
Signature: ...

Code: ...
```

Embedding the metadata alongside code gives semantic search more context than embedding raw source alone.

### Step 8: send to Python

`CodeChunk_ToPython.ToPythonChunk` converts C# property naming to the shape expected by FastAPI.

`FastApiService.EmbedProjectAsync` sends:

```csharp
new
{
    chunks = chunks.Select(...)
}
```

This is an anonymous type: a temporary object whose property shape is inferred by the compiler.

### Important limitation

Chunk extraction is syntactic. It may know a method is named `Search`, but it does not resolve which external or overloaded methods are called inside that method.

---

## 12. SymbolIdentityService: exact semantic identity

File: `src/Lavender.Infrastructure/Indexing/Symbol/SymbolIdentityService.cs`

### Purpose

Every service needs one compatible answer to:

> Which exact C# declaration is this?

### Display format

`SymbolDisplayFormat` controls how Roslyn converts a symbol to text.

The identity format includes:

- Global namespace qualification
- Containing namespaces and types
- Generic parameters and variance
- Containing member type
- Parameters
- Parameter types
- `ref`, `out`, `in`, and `params`
- Explicit interface names
- Nullable annotations

This is why overloads produce different identity input.

### Canonicalization

```csharp
ISymbol canonical =
    symbol is IMethodSymbol { ReducedFrom: not null } method
        ? method.ReducedFrom.OriginalDefinition
        : symbol.OriginalDefinition;
```

#### `OriginalDefinition`

For a constructed generic such as `List<string>`, Roslyn can expose a constructed symbol. `OriginalDefinition` normalizes it to the declaration form such as `List<T>`.

#### `ReducedFrom`

An extension method called like:

```csharp
items.Where(predicate)
```

may be represented as a reduced method where the first `this` parameter has been absorbed by instance-call syntax. `ReducedFrom` points back to the original extension method declaration.

### Hashing

The readable identity includes:

```text
symbol kind | assembly identity | fully qualified display
```

It is converted to UTF-8 bytes, hashed with SHA-256, and encoded as hexadecimal.

The hash makes dictionary keys compact and avoids awkward punctuation in IDs. It is not being used for security.

---

## 13. SymbolIndexingService

Files:

- `src/Lavender.Core/DataTypes/CodeSymbol.cs`
- `src/Lavender.Infrastructure/Indexing/Symbol/SymbolIndexingService.cs`
- `src/Lavender.Infrastructure/Indexing/Symbol/SymbolIndex.cs`

### Purpose

This service creates:

1. Serializable `CodeSymbol` metadata for querying and display.
2. A lookup from the same ID to Roslyn's live `ISymbol`.

### Step 1: iterate projects

```csharp
foreach (Project project in context.Solution.Projects)
```

A solution can contain multiple projects. Each project has its own compilation.

### Step 2: request the compilation once

```csharp
Compilation? compilation =
    await project.GetCompilationAsync(cancellationToken);
```

The compilation is reused for all documents in that project.

### Step 3: iterate documents

For every document, the service requests:

```csharp
SyntaxNode? root = await document.GetSyntaxRootAsync(...);
SyntaxTree? tree = await document.GetSyntaxTreeAsync(...);
```

It then creates:

```csharp
SemanticModel model = compilation.GetSemanticModel(tree);
```

One semantic model connects that document's syntax to the project's compilation.

### Step 4: discover declaration syntax

`GetDeclarations` yields:

- Every `MemberDeclarationSyntax`
- Field variable declarators
- Enum members

Fields need special handling because:

```csharp
private int x, y;
```

is one field declaration syntax containing two `VariableDeclaratorSyntax` nodes, but semantically `x` and `y` are separate symbols.

### Step 5: convert syntax to symbols

```csharp
model.GetDeclaredSymbol(node, token)
```

This asks Roslyn, “which symbol does this declaration introduce?”

The switch expression selects the correct typed overload for types, methods, properties, fields, events, delegates, and enum members.

### Step 6: create `CodeSymbol`

`CreateModel` stores:

- Canonical ID
- Simple name
- Fully qualified display
- Lavender symbol category
- Namespace
- Containing type
- Signature
- Absolute and relative paths
- One-based start/end lines

The `GetSymbolType` switch uses Roslyn properties such as:

```csharp
INamedTypeSymbol { TypeKind: TypeKind.Interface }
IMethodSymbol { MethodKind: MethodKind.Constructor }
IPropertySymbol { IsIndexer: true }
```

### Step 7: construct `SymbolIndex`

`SymbolIndex` maintains two dictionaries:

```text
symbol ID -> CodeSymbol
symbol ID -> ISymbol
```

`TryAdd` is important for partial types. Multiple declarations can produce the same canonical ID. The dictionary keeps one main metadata entry and one Roslyn symbol whose `DeclaringSyntaxReferences` can still describe all declarations.

### Searching

`Find` performs case-insensitive substring search over simple and fully qualified names. This is exact metadata search, not vector search.

---

## 14. Relationship indexing

Files:

- `CodeRelationship.cs`
- `CodeRelationshipIndexer.cs`
- `CodeRelationshipGraph.cs`

### Graph vocabulary

A graph consists of:

- **Nodes**: symbols identified by symbol ID.
- **Edges**: directed relationships from a source node to a target node.

Examples:

```text
Service --InheritsFrom--> BaseService
Service --Implements--> IService
Execute --Calls--> Helper
Execute --UsesType--> ResultModel
```

### Relationship model

Each edge stores:

- Source and target IDs
- Relationship type enum
- File and one-based call/use position
- Display names
- Whether the target is outside the loaded solution

### Step 1: prepare deduplication and internal IDs

```csharp
var seen = new HashSet<string>(StringComparer.Ordinal);
var internalIds = symbols.Symbols
    .Select(s => s.Id)
    .ToHashSet(StringComparer.Ordinal);
```

`seen` prevents duplicate edges. `internalIds` determines whether a target is external.

### Step 2: inheritance

For each `BaseTypeDeclarationSyntax`, the semantic model resolves an `INamedTypeSymbol`.

```csharp
source.BaseType
```

returns the compiler-resolved base class.

The property pattern:

```csharp
source.BaseType is
{
    SpecialType: not SpecialType.System_Object
} baseType
```

both checks for a base type and excludes `System.Object`.

### Step 3: interfaces

```csharp
foreach (INamedTypeSymbol iface in source.Interfaces)
```

`Interfaces` means interfaces directly declared by that type. `AllInterfaces` would include inherited/transitive interfaces and could create many redundant edges.

### Step 4: find the owning symbol

For every syntax node, `GetOwner` walks:

```csharp
node.AncestorsAndSelf()
```

It looks for the nearest method, property, type, or variable declaration. That declaration is resolved with `GetDeclaredSymbol`.

If no supported declaration is found, it asks:

```csharp
model.GetEnclosingSymbol(node.SpanStart, token)
```

This returns the semantic symbol containing a source position.

Only internal owners are used as edge sources. Lavender does not index method bodies from external assemblies.

### Step 5: resolve calls

The syntax switch recognizes:

- `InvocationExpressionSyntax`: `Helper()`
- `ObjectCreationExpressionSyntax`: `new ResultModel()`
- `ImplicitObjectCreationExpressionSyntax`: `ResultModel x = new()`
- `ConstructorInitializerSyntax`: `: base(...)` or `: this(...)`

For each:

```csharp
model.GetSymbolInfo(expression).Symbol
```

returns the method or constructor selected by compiler overload resolution.

Object construction therefore creates a `Calls` edge to the constructor. Type syntax can separately create a `UsesType` edge to the constructed type.

### Step 6: resolve type usage

For every `TypeSyntax`:

```csharp
ITypeSymbol? type =
    model.GetTypeInfo(typeSyntax).Type;
```

`ExpandTypes` recursively handles:

- Arrays by examining `ElementType`
- Pointers by examining `PointedAtType`
- Named types
- Generic type arguments

For:

```csharp
List<SearchResult>
```

the service yields the original definition of `List<T>` and the named type `SearchResult`.

### Step 7: deduplicate

Inheritance, implementation, and type-use keys contain:

```text
source ID | target ID | relationship type
```

Call keys additionally contain path and syntax position. This means repeated calls at different source locations are retained.

### Step 8: build query indexes

`CodeRelationshipGraph` groups edges into:

```text
source ID -> outgoing edges
target ID -> incoming edges
```

This makes caller/callee queries cheap:

- Callees are outgoing `Calls`.
- Callers are incoming `Calls`.
- Derived types are incoming `InheritsFrom`.
- Implementations are incoming `Implements`.

---

## 15. Exact source retrieval

File: `src/Lavender.Infrastructure/Source/SymbolSourceService.cs`

### Purpose

Return source based on an exact symbol ID, without guessing from vector chunks.

### Step 1: ID to Roslyn symbol

```csharp
ISymbol? symbol = _index.GetRoslynSymbol(symbolId);
```

Unknown IDs return no result.

### Step 2: declaration references

```csharp
symbol.DeclaringSyntaxReferences
```

Roslyn symbols declared in source carry references back to their declaration syntax.

Metadata-only symbols, such as framework methods, usually have no declaring syntax references in the loaded solution.

### Step 3: resolve syntax

```csharp
SyntaxNode node =
    await reference.GetSyntaxAsync(cancellationToken);
```

For a method, this is the complete method declaration node, including its body. For a property or constructor, it is the corresponding complete declaration.

### Step 4: return exact text and location

```csharp
node.ToFullString()
```

`ToFullString` includes the node's full source text and trivia owned by that node. In Roslyn, **trivia** means whitespace, comments, and directives that do not form normal language tokens.

### Partial types

A partial type can have multiple `DeclaringSyntaxReferences`.

- `GetSourceBySymbolIdAsync` returns the first declaration.
- `GetAllDeclarationsAsync` returns every declaration.

---

## 16. Diagnostics provider

File: `src/Lavender.Infrastructure/Diagnostics/RoslynDiagnosticsProvider.cs`

### Purpose

Expose compiler diagnostics as data an AI tool or UI can filter and display.

### Solution/project/document queries

- Solution diagnostics iterate all projects.
- Project diagnostics match by Roslyn project ID or project name.
- Document diagnostics currently collect solution diagnostics and filter by normalized full path.

### Step 1: compilation diagnostics

```csharp
foreach (Diagnostic diagnostic
         in compilation.GetDiagnostics(token))
```

This includes syntax and compiler semantic diagnostics. A broken project can still produce a compilation and useful error diagnostics.

### Step 2: source location

```csharp
Location location = diagnostic.Location;
```

Not all diagnostics have source locations. Assembly-level and command-line diagnostics may have none.

For source diagnostics:

```csharp
location.GetLineSpan()
```

provides file and line/column information.

### Step 3: associate a symbol

The service:

1. Gets the semantic model for the diagnostic's syntax tree.
2. Gets the tree root.
3. Calls `FindNode` for the diagnostic character span.
4. Walks the node and its ancestors.
5. Calls `GetDeclaredSymbol` until one resolves.
6. Falls back to `GetEnclosingSymbol`.
7. Generates the shared Lavender symbol ID.

This mapping is best effort. The diagnostic is still returned when no symbol can be identified.

---

## 17. Project dependency graph

File: `src/Lavender.Infrastructure/Indexing/Dependencies/ProjectDependencyGraph.cs`

### Purpose

Describe build-level relationships rather than relationships inside C# method bodies.

### Node types

- Solution
- Project
- Package
- Assembly

### Edge types

```text
Solution --Contains--> Project
Project --ReferencesProject--> Project
Project --ReferencesPackage--> Package
Project --ReferencesAssembly--> Assembly
```

### Step 1: solution and projects

The solution ID uses Roslyn's `Solution.Id`. Each project ID uses its Roslyn `ProjectId`.

Target framework is read from the project XML.

### Step 2: project references

```csharp
foreach (ProjectReference reference
         in project.ProjectReferences)
```

Roslyn has already evaluated these project-to-project references.

### Step 3: package references

Roslyn metadata references do not preserve a simple, reliable original NuGet package name. Therefore the service parses `.csproj` XML with `XDocument`.

It recognizes:

```xml
<PackageReference Include="Example" Version="1.2.3" />
```

and:

```xml
<PackageReference Include="Example">
  <Version>1.2.3</Version>
</PackageReference>
```

`Name.LocalName` is used so XML namespaces do not prevent matching element names.

This parser does not run full MSBuild or NuGet property evaluation.

### Step 4: assembly references

```csharp
project.MetadataReferences
```

These are compiler references to assemblies. Nodes are deduplicated by normalized assembly filename.

### Step 5: graph queries

Like the code graph:

- `GetDependencies(id)` follows outgoing edges.
- `GetDependents(id)` follows incoming edges.
- `GetPackages(projectId)` filters package edges.
- `GetProjectReferences(projectId)` filters project-reference edges.

---

## 18. GitContextService

File: `src/Lavender.Infrastructure/Git/GitContextService.cs`

### Purpose

Provide read-only working-copy context without adding a Git library dependency.

### Process execution

```csharp
var psi = new ProcessStartInfo("git")
{
    WorkingDirectory = _workingDirectory,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true
};
```

The service starts `git` directly and captures both output streams.

Arguments are added with:

```csharp
psi.ArgumentList.Add(arg);
```

This avoids constructing a shell command string from file paths.

### Status

The service first runs:

```text
git rev-parse --show-toplevel
```

This detects the repository root.

Then it runs:

```text
git branch --show-current
git status --porcelain=v1 -z
```

Porcelain format is intended for programs. `-z` uses null characters between paths, which safely handles spaces and most unusual filename characters.

The first two status characters are:

```text
XY
```

- `X`: index/staged status
- `Y`: working-tree status

### Diff

- `git diff` returns unstaged changes.
- `git diff --cached` returns staged changes.
- `-- <path>` limits the diff to one path.

The raw unified diff is returned because it is already useful AI context.

### Recent commits

The log format uses field and record separator control characters. This is safer than attempting to split human-oriented log output on spaces.

### Failure handling

`CommandResult` retains:

- Exit code
- Standard output
- Standard error
- Startup error

Missing Git and “not a repository” are normal structured results rather than vague crashes.

---

## 19. ProjectKnowledgeService

File: `src/Lavender.Infrastructure/Knowledge/ProjectKnowledgeService.cs`

### Purpose

This facade gives a future AI tool layer one read-only entry point.

It does not duplicate indexing logic. Each method delegates to the appropriate service.

Example:

```csharp
var symbol = knowledgeService
    .FindSymbols("SearchProjectAsync")
    .First();

var source = await knowledgeService
    .GetSymbolSourceAsync(symbol.Id);

var callers = knowledgeService.GetCallers(symbol.Id);
var callees = knowledgeService.GetCallees(symbol.Id);

var diagnostics =
    await knowledgeService.GetDiagnosticsAsync();

var diff = await knowledgeService.GetGitDiffAsync(
    symbol.RelativePath,
    staged: false);
```

This sequence demonstrates why shared identity matters. The symbol found in the first operation supplies the ID used by source and graph operations.

---

## Part IV: Worked example

Consider:

```csharp
interface IService
{
    void Execute();
}

class BaseService
{
    public virtual void Start() { }
}

class Service : BaseService, IService
{
    public void Execute()
    {
        Helper();
        var model = new ResultModel();
    }

    private void Helper() { }
}

class ResultModel
{
}
```

### Chunk index

Likely chunks include:

- Whole file
- File summary
- `BaseService.Start`
- `Service.Execute`
- `Service.Helper`

The `Execute` chunk contains its source and embedding metadata.

### Symbol index

Symbols include:

- `IService`
- `IService.Execute`
- `BaseService`
- `BaseService.Start`
- `Service`
- `Service.Execute`
- `Service.Helper`
- `ResultModel`
- The implicit or explicit constructors Roslyn exposes only where declarations are indexed

Each indexed declaration gets a canonical ID.

### Relationships

```text
Service --InheritsFrom--> BaseService
Service --Implements--> IService
Service.Execute --Calls--> Service.Helper
Service.Execute --Calls--> ResultModel constructor
Service.Execute --UsesType--> ResultModel
```

### Source retrieval

Using the ID for `Service.Execute` returns:

```csharp
public void Execute()
{
    Helper();
    var model = new ResultModel();
}
```

### Diagnostics

If `Helper` did not exist, the compilation would report an error. The provider would return the compiler ID, message, file, position, project, and—when resolvable—the ID of `Service.Execute`.

---

## Part V: How to study and debug this system

### 20. Recommended reading order

Read the code in this order:

1. `CodeChunk.cs`
2. `CodeSymbol.cs`
3. `IndexedProjectContext.cs`
4. `ProjectIndexer.cs`
5. `CodeChunkService.cs`
6. `SymbolIdentityService.cs`
7. `SymbolIndexingService.cs`
8. `SymbolIndex.cs`
9. `CodeRelationship.cs`
10. `CodeRelationshipIndexer.cs`
11. `CodeRelationshipGraph.cs`
12. `SymbolSourceService.cs`
13. `RoslynDiagnosticsProvider.cs`
14. `ProjectDependencyGraph.cs`
15. `GitContextService.cs`
16. `ProjectKnowledgeService.cs`

### 21. Useful debugger breakpoints

Set breakpoints at:

- `ProjectIndexer.IndexProjectAsync`
- `IndexedProjectContext.OpenAsync`
- `SymbolIndexingService.IndexAsync`
- `SymbolIndexingService.CreateModel`
- `CodeRelationshipIndexer.Add`
- `SymbolSourceService.GetAllDeclarationsAsync`
- `RoslynDiagnosticsProvider.GetProjectAsync`

Inspect:

- `project.Name`
- `document.FilePath`
- `tree.FilePath`
- `symbol.Kind`
- `symbol.ToDisplayString()`
- `_identity.GetId(symbol)`
- `called`
- `type`
- `result.Count`

### 22. Questions to ask while stepping

For each syntax node:

1. What text produced this node?
2. What is its concrete syntax type?
3. What is its `Span`?
4. Which declaration contains it?
5. What does `GetDeclaredSymbol` return?
6. What does `GetSymbolInfo` return?
7. What does `GetTypeInfo` return?
8. Is the target internal or external?
9. Which ID is generated?
10. Which graph direction should the edge use?

### 23. Common Roslyn mistakes

#### Confusing declaration and reference APIs

- Use `GetDeclaredSymbol` for a declaration.
- Use `GetSymbolInfo` for an expression or name that refers to something.
- Use `GetTypeInfo` when you need the type of syntax.

#### Reopening the workspace

Reuse `IndexedProjectContext`. Do not make every service call `MSBuildWorkspace.Create`.

#### Comparing symbols by display text alone

Display text is helpful, but canonical identity must include enough signature information to distinguish overloads.

#### Forgetting zero-based Roslyn positions

Roslyn line and column positions are zero-based. Lavender's result models add one.

#### Treating unresolved code as fatal

Incomplete projects are normal in a code assistant. Null semantic results should usually skip one edge or symbol rather than abort the entire index.

#### Holding workspace objects after disposal

Source retrieval and diagnostics require the indexed context to remain alive.

---

## Part VI: Current boundaries

The current implementation intentionally does not yet provide:

- Persistent graph storage
- Incremental per-document reindexing
- Local-function symbols
- Separate property/event accessor symbols
- Member-level interface implementation edges
- Override, read, write, or return edges
- Full analyzer execution through `CompilationWithAnalyzers`
- Full MSBuild/NuGet evaluation of conditional package versions
- Parsed Git diff hunks
- AI tool definitions or an autonomous editing agent

The next architectural step is to expose selected `IProjectKnowledgeService` operations as controlled, read-only tools. Before that, focused tests should be added for symbol overload identity, partial declarations, relationships, diagnostics, Git porcelain parsing, and package-reference parsing.

---

## Short mental model to remember

```text
Syntax tree:
    What was written?

Semantic model:
    What does it mean in this compilation?

Chunk:
    What text should semantic search retrieve?

Symbol:
    Which exact declaration is this?

Relationship:
    How do two exact symbols connect?

Source service:
    What is the authoritative declaration text?

Diagnostics:
    What does the compiler report?

Dependency graph:
    What does this project build against?

Git context:
    What changed in the working repository?

Knowledge facade:
    How can a future tool query all of the above safely?
```
