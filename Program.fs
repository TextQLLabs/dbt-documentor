open System
open System.Collections.Generic
open System.IO
open Argu
open RestSharp
open RestSharp.Authenticators.OAuth2
open FSharp.Json
open YamlDotNet.RepresentationModel
open YamlDotNet.Serialization

type OAIRequest =
    { model: string
      prompt: string
      temperature: double
      max_tokens: int }
and OAIRequestWithUserInfo=
     { prompt: string
       email: string }
and OAIResponse = { choices: OAIChoice [] }
and OAIChoice = { text: string }

and Manifest = { nodes: Map<string, NodeMetadata> }

and NodeMetadata =
    { original_file_path: string
      patch_path: string option
      compiled_code: string
      raw_code: string
      description: string
      name: string
      unique_id: string
      fqn: string []
      refs: string [] []
      columns: Map<string, ColumnMetadata>
      depends_on: Depends }

and ColumnMetadata = { name: string; description: string }
and Depends = { nodes: string []; macros: string [] }

and Env =
    { apiKey: KeyOrUserInfo
      basePath: string
      projectName: string
      models: HashSet<string> option
      dry_run: bool }

and KeyOrUserInfo =
    | Key of string
    | UserInfo of string

and Arguments =
    | Working_Directory of path: string
    | Gen_Undocumented
    | Gen_Specific of models_list: string list
    | Dry_Run
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Working_Directory _ -> "DBT project root (default: .)"
            | Gen_Undocumented ->
                "Generate docs for all undocumented models (default: enabled, disabled by --gen-specific)"
            | Gen_Specific _ ->
                "Generate docs only for specified model names (comma-separated list) (default: none, disabled by --gen-undocumented)"
            | Dry_Run -> "Don't write any docs, print them to the command line. (default: false)"

and ArgsConfig =
    { workingDirectory: string
      genMode: GenMode
      dry_run: bool }

and GenMode =
    | Undocumented
    | Specific of string list

let mkPrompt (reverseDeps: Dictionary<string, List<string>>) (node: NodeMetadata) =
    let deps =
        String.concat "," node.depends_on.nodes

    let rDeps =
        if reverseDeps.ContainsKey(node.unique_id) then
            String.concat "," reverseDeps[node.unique_id]
        else
            "Not used by any other models"

    let staging =
        if Array.contains "staging" node.fqn then
            "\nThis is a staging model. Be sure to mention that in the summary.\n"
        else
            ""

    $@"Write markdown documentation to explain the following DBT model. Be clear and informative, but also accurate. The only information available is the metadata below.
Explain the raw SQL, then explain the dependencies. Do not list the SQL code or column names themselves; an explanation is sufficient.

Model name: {node.name}
Raw SQL code: {node.raw_code}
Depends on: {deps}
Depended on by: {rDeps}
{staging}
First, generate a human-readable name for the table as the title (i.e. fct_orders -> # Orders Fact Table).
Then, describe the dependencies (both model dependencies and the warehouse tables used by the SQL.) Do this under ## Dependencies.
Then, describe what other models reference this model in ## How it's used
Then summarize the model logic in ## Summary.
"

let mkColumnPrompt (node: NodeMetadata) (col: ColumnMetadata) : string =
    $@"Write markdown documentation to explain the following DBT column in the context of the parent model and SQL code. Be clear and informative, but also accurate. The only information available is the metadata below.
Do not list the SQL code or column names themselves; an explanation is sufficient.

Column Name: {col.name}
Parent Model name: {node.name}
Raw SQL code: {node.raw_code}

First, explain the meaning of the column in plain, non-technical English.false
Then, explain how the column is extracted in code.
"

type SummarizedResult =
    { patch_path: string option
      summary: string
      original_file_path: string
      columnSummaries: Map<string, string>
      name: string }

let stdoutLock = Object()

let runOpenAIRequest (env: Env) (prompt: string) : Async<string> =
    async {
        let temp = 0.2

        let baseReq: OAIRequest =
            { model = "text-davinci-003"
              prompt = prompt
              temperature = temp
              max_tokens = 1000 }

        let request, options =
            match env.apiKey with
            | Key k ->
                let url =
                    "https://api.openai.com/v1/completions"

                let options = RestClientOptions(url)
                let request = RestRequest()
                let _ = request.AddJsonBody(baseReq)
                options.Authenticator <- OAuth2AuthorizationRequestHeaderAuthenticator(k, "Bearer")
                (request, options)
            | UserInfo email  ->
                let url = "https://api.textql.com/api/oai"
                let options = RestClientOptions(url)
                let request = RestRequest()
                let body = {
                    prompt = prompt
                    email = email
                }
                let _ = request.AddJsonBody(body)
                (request, options)

        let client = new RestClient(options)
        let! response = Async.AwaitTask(client.PostAsync(request))

        let result =
            Json.deserialize<OAIResponse> response.Content

        return result.choices[0].text
    }

let genColumnSummaries (env: Env) (node: NodeMetadata) : Async<Map<string, string>> =
    async {
        let prefix = "[ai-gen] "

        let mapper (k, column) =
            async {
                let! result = runOpenAIRequest env (mkColumnPrompt node column)
                return (k, prefix + result)
            }

        let! resultSeq =
            node.columns
            |> Map.filter (fun _k v -> v.description.Equals(""))
            |> Map.toSeq
            |> Seq.map mapper
            |> Async.Parallel

        return (Map.ofSeq resultSeq)
    }

let openAISummarize
    (env: Env)
    (reverseDeps: Dictionary<string, List<string>>)
    (node: NodeMetadata)
    : Async<SummarizedResult> =
    async {
        lock stdoutLock (fun _ -> printfn $"Generating docs for: {node.name}")

        let summaryPrefix =
            "This description is generated by an AI model. Take it with a grain of salt!\n"

        let! result = runOpenAIRequest env (mkPrompt reverseDeps node)

        let! columnSummaries = genColumnSummaries env node

        return
            { patch_path = node.patch_path
              name = node.name
              original_file_path = node.original_file_path
              summary = summaryPrefix + result
              columnSummaries = columnSummaries }
    }

let insertColumnDescription
    env
    (nodeResult: SummarizedResult)
    (colMap: Map<string, string>)
    (modelNode: YamlNode)
    : unit =
    let modelNode' =
        modelNode :?> YamlMappingNode

    let nameNode =
        modelNode'.Children[YamlScalarNode("name")] :?> YamlScalarNode

    let name = nameNode.Value

    match Map.tryFind name colMap with // If it's in the node map it shouldn't have a description or it's the empty string
    | None -> ()
    | Some colResult ->
        let docName =
            "tql_generated_doc__"
            + nodeResult.name
            + "__"
            + name

        let mdPath =
            String.concat
                "/"
                [ env.basePath
                  Path.GetDirectoryName(nodeResult.original_file_path)
                  docName + ".md" ]

        let header = "{% docs " + docName + " %}"
        let footer = "{% enddocs %}"

        let docContent =
            String.concat "\n" [ header; colResult; footer ]

        lock stdoutLock (fun _ -> printfn $"Writing new docs to: {mdPath}")

        if env.dry_run then
            printfn $"{docContent}"
        else
            File.WriteAllText(mdPath, docContent)

        let _ =
            modelNode'.Children.Remove(YamlScalarNode("description"))

        modelNode'.Children.Add(YamlScalarNode("description"), YamlScalarNode("{{ doc(\"" + docName + "\") }}"))

let insertDescription env (nodeMap: Map<string, SummarizedResult>) (modelNode: YamlNode) : unit =
    let modelNode' =
        modelNode :?> YamlMappingNode

    let nameNode =
        modelNode'.Children[YamlScalarNode("name")] :?> YamlScalarNode

    let name = nameNode.Value

    match Map.tryFind name nodeMap with // If it's in the node map it shouldn't have a description or it's the empty string
    | None -> ()
    | Some node ->
        let docName =
            "tql_generated_doc__" + node.name

        let mdPath =
            String.concat
                "/"
                [ env.basePath
                  Path.GetDirectoryName(node.original_file_path)
                  docName + ".md" ]

        let header = "{% docs " + docName + " %}"
        let footer = "{% enddocs %}"

        let docContent =
            String.concat "\n" [ header; node.summary; footer ]

        lock stdoutLock (fun _ -> printfn $"Writing new docs to: {mdPath}")

        if modelNode'.Children.ContainsKey(YamlScalarNode("columns")) then
            let colsNode =
                modelNode'.Children[YamlScalarNode("columns")] :?> YamlSequenceNode

            colsNode
            |> Seq.iter (insertColumnDescription env node node.columnSummaries)

        if env.dry_run then
            printfn $"{docContent}"
        else
            File.WriteAllText(mdPath, docContent)

        let _ =
            modelNode'.Children.Remove(YamlScalarNode("description"))

        modelNode'.Children.Add(YamlScalarNode("description"), YamlScalarNode("{{ doc(\"" + docName + "\") }}"))

let insertDocs (env: Env) (patchPathMay: string option, nodes: SummarizedResult seq) : unit =
    match patchPathMay with
    | None -> ()
    | Some patchPath ->
        let path =
            env.basePath
            + "/"
            + patchPath.Replace(env.projectName + "://", "")

        let contents = File.ReadAllText(path)

        let deserializer =
            let builder = DeserializerBuilder()

            builder.Build()

        let config =
            deserializer.Deserialize<YamlMappingNode>(contents)

        let models = YamlScalarNode("models")

        let resultMap =
            nodes
            |> Seq.fold (fun m n -> Map.add n.name n m) Map.empty

        let modelsNode =
            (config.Children[models] :?> YamlSequenceNode)

        modelsNode
        |> Seq.iter (insertDescription env resultMap)

        let serializer = SerializerBuilder().Build()
        let yaml = serializer.Serialize(config)
        lock stdoutLock (fun _ -> printfn $"Adding description to {Seq.length nodes} models in {path}")

        if env.dry_run then
            printfn $"{yaml}"
        else
            File.WriteAllText(path, yaml)

let readProjectConfig (basePath: string) : string =
    let path = basePath + "/dbt_project.yml"
    let contents = File.ReadAllText(path)

    let deserializer =
        DeserializerBuilder().Build()

    let config =
        deserializer.Deserialize<YamlMappingNode>(contents)

    let nameNode =
        config.Children[YamlScalarNode("name")] :?> YamlScalarNode

    nameNode.Value

let isModel (name: string) =
    let nodeType = name.Split('.')[0]
    nodeType.Equals("model")

let shouldWriteDoc (env: Env) (pair: KeyValuePair<string, NodeMetadata>) : bool =
    let pred nm =
        match env.models with
        | None -> pair.Value.description.Equals("")
        | Some models -> models.Contains nm

    isModel pair.Key && pred pair.Value.name

let mkReverseDependencyMap (nodes: Map<string, NodeMetadata>) =
    let ans: Dictionary<string, List<string>> =
        Dictionary()

    let folder () (nm: string) (metadata: NodeMetadata) =
        if isModel nm then
            for modelDep in metadata.depends_on.nodes do
                if ans.ContainsKey modelDep then
                    ans[ modelDep ].Add nm
                else
                    ans[modelDep] <- ResizeArray [ nm ]

    nodes |> Map.fold folder ()
    ans

exception ApiKeyNotFound of unit

let parseArgs argv =
    let foldArgs config0 arg =
        match arg with
        | Working_Directory path -> { config0 with workingDirectory = path }
        | Gen_Undocumented -> { config0 with genMode = Undocumented }
        | Gen_Specific models_list -> { config0 with genMode = Specific models_list }
        | Dry_Run -> { config0 with dry_run = true }

    let config0: ArgsConfig =
        { workingDirectory = "./"
          genMode = Undocumented
          dry_run = false }

    let parser =
        ArgumentParser.Create<Arguments>(programName = "DbtHelper")

    let results = parser.Parse(argv)
    let all = results.GetAllResults()
    Seq.fold foldArgs config0 all

[<EntryPoint>]
let main argv =
    let init: (Manifest * Env) option =
        try
            let argsEnv = parseArgs argv

            let contents =
                try
                    File.ReadAllText(argsEnv.workingDirectory + "/target/manifest.json")
                with
                | e ->
                    printfn "Reading target/manifest.json failed. Please re-run from a dbt project with generated docs"
                    raise e

            let manifest =
                try
                    Json.deserialize<Manifest> contents
                with
                | e ->
                    printfn "manifest.json deserialization failed"
                    raise e

            let projectName =
                try
                    readProjectConfig argsEnv.workingDirectory
                with
                | e ->
                    printfn "Reading dbt_project.yml failed. Please re-run from a dbt project root."
                    raise e

            let apiKey =
                match Environment.GetEnvironmentVariable("OPENAI_API_KEY") with
                | null ->
                    printfn "You haven't specified an API Key. No worries, this one's on TextQL!"
                    printfn "In return, please type your email address. We don't collect any other data, nor sell your email to third parties."
                    printfn "If you're okay with this, press enter. Otherwise, type 'no' and set the OPENAI_API_KEY environment variable."
                    printf "Email (type no to abort): "
                    let email =  Console.ReadLine ()
                    if email.Equals "no"
                        then raise (ApiKeyNotFound ())
                        else UserInfo email
                | k -> Key k

            let models =
                match argsEnv.genMode with
                | Undocumented -> None
                | Specific ls -> Some(HashSet(ls))

            Some(
                manifest,
                { apiKey = apiKey
                  basePath = argsEnv.workingDirectory
                  projectName = projectName
                  models = models
                  dry_run = argsEnv.dry_run }
            )
        with
        | :? ArguParseException as e ->
            printfn $"{e.Message}"
            None
        | e ->
            printfn "Initialization failed. Aborting"
            printfn $"{e}"
            None

    match init with
    | None -> 1
    | Some (manifest, env) ->
        if env.dry_run then
            printfn "Dry Run. Results will not be written."

        let rDeps =
            mkReverseDependencyMap manifest.nodes

        let limitedPar fn = Async.Parallel(fn, 4)

        manifest.nodes
        |> Seq.filter (shouldWriteDoc env)
        |> Seq.map (fun x -> openAISummarize env rDeps x.Value)
        |> limitedPar
        |> Async.RunSynchronously
        |> Seq.groupBy (fun x -> x.patch_path)
        |> Seq.iter (insertDocs env)

        printfn "Success! Make sure to run `dbt docs generate`."

        0
