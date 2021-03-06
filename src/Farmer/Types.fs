namespace Farmer

/// Represents a name of an ARM resource
type ResourceName =
    | ResourceName of string
    static member Empty = ResourceName ""
    member this.Value =
        let (ResourceName path) = this
        path
    member this.IfEmpty fallbackValue =
        match this with
        | r when r = ResourceName.Empty -> ResourceName fallbackValue
        | r -> r
    member this.Map mapper = match this with ResourceName r -> ResourceName (mapper r)
    static member (+) (a:ResourceName, b) = ResourceName(a.Value + "/" + b)
    static member (+) (a:ResourceName, b:ResourceName) = a + b.Value

type Location =
    | Location of string
    member this.ArmValue = match this with Location location -> location.ToLower()

/// An Azure ARM resource value which can be mapped into an ARM template.
type IArmResource =
    /// The name of the resource, to uniquely identify against other resources in the template.
    abstract member ResourceName : ResourceName
    /// A raw object that is ready for serialization directly to JSON.
    abstract member JsonModel : obj

/// Represents a high-level configuration that can create a set of ARM Resources.
type IBuilder =
    /// Given a location and the currently-built resources, returns a set of resource actions.
    abstract member BuildResources : Location -> IArmResource list
    /// Provides the resource name that other resources should use when depending upon this builder.
    abstract member DependencyName : ResourceName

namespace Farmer.CoreTypes

open Farmer
open System

type ResourceType =
    | ResourceType of path:string * version:string
    /// Returns the ARM resource type string value.
    member this.Type = match this with ResourceType (p, _) -> p
    member this.ApiVersion = match this with ResourceType (_, v) -> v
    member this.Create(name:ResourceName, ?location:Location, ?dependsOn:ResourceName list, ?tags:Map<string,string>) =
        match this with
        ResourceType (path, version) ->
            {| ``type`` = path
               apiVersion = version
               name = name.Value
               location = location |> Option.map(fun r -> r.ArmValue) |> Option.toObj
               dependsOn = dependsOn |> Option.map (List.map(fun r -> r.Value) >> box) |> Option.toObj
               tags = tags |> Option.map box |> Option.toObj |}

/// Represents an expression used within an ARM template
type ArmExpression =
    private | ArmExpression of string * ResourceName option
    static member create (rawText:string, ?resourceName) =
        if System.Text.RegularExpressions.Regex.IsMatch(rawText, @"^\[.*\]$") then
            failwithf "ARM Expressions should not be wrapped in [ ]; these will automatically be added when the expression is evaluated. Please remove them from '%s'." rawText
        else
            ArmExpression(rawText, resourceName)
    /// Gets the raw value of this expression.
    member this.Value = match this with ArmExpression (e, _) -> e
    /// Tries to get the owning resource of this expression.
    member this.Owner = match this with ArmExpression (_, o) -> o
    /// Applies a mapping function to the expression.
    member this.Map mapper = match this with ArmExpression (e, r) -> ArmExpression(mapper e, r)
    /// Evaluates the expression for emitting into an ARM template. That is, wraps it in [].
    member this.Eval() = sprintf "[%s]" this.Value
    member this.WithOwner(owner:ResourceName) = match this with ArmExpression (e, _) -> ArmExpression(e, Some owner)

    /// Evaluates the expression for emitting into an ARM template. That is, wraps it in [].
    static member Eval (expression:ArmExpression) = expression.Eval()
    static member Empty = ArmExpression ("", None)
    /// Builds a resourceId ARM expression from the parts of a resource ID.
    static member resourceId (resourceType:ResourceType, name:ResourceName, ?group:string, ?subscriptionId:string) =
        match name, group, subscriptionId with
        | name, Some group, Some sub -> sprintf "resourceId('%s', '%s', '%s', '%s')" sub group resourceType.Type name.Value
        | name, Some group, None -> sprintf "resourceId('%s', '%s', '%s')" group resourceType.Type name.Value
        | name, None, _ -> sprintf "resourceId('%s', '%s')" resourceType.Type name.Value
        |> ArmExpression.create
    static member resourceId (resourceType:ResourceType, [<ParamArray>] resourceSegments:ResourceName []) =
        sprintf
            "resourceId('%s', %s)"
            resourceType.Type
            (resourceSegments |> Array.map (fun r -> sprintf "'%s'" r.Value) |> String.concat ", ")
        |> ArmExpression.create
    static member reference (resourceType:ResourceType, resourceId:ArmExpression) =
        sprintf "reference(%s, '%s')" resourceId.Value resourceType.ApiVersion
        |> ArmExpression.create

/// A secure parameter to be captured in an ARM template.
type SecureParameter =
    | SecureParameter of name:string
    member this.Value = match this with SecureParameter value -> value
    /// Gets an ARM expression reference to the parameter e.g. parameters('my-password')
    member this.AsArmRef = sprintf "parameters('%s')" this.Value |> ArmExpression.create

/// Exposes parameters which are required by a specific IArmResource.
type IParameters =
    abstract member SecureParameters : SecureParameter list

/// An action that needs to be run after the ARM template has been deployed.
type IPostDeploy =
    abstract member Run : resourceGroupName:string -> Option<Result<string, string>>

/// A functional equivalent of the IBuilder's BuildResources method.
type Builder = Location -> IArmResource list

[<AutoOpen>]
module ArmExpression =
    /// A helper function used when building complex ARM expressions; lifts a literal string into a
    /// quoted ARM expression e.g. text becomes 'text'. This is useful for working with functions
    /// that can mix literal values and parameters.
    let literal = sprintf "'%s'" >> ArmExpression.create
    /// Generates an ARM expression for concatination.
    let concat values =
        values
        |> Seq.map(fun (r:ArmExpression) -> r.Value)
        |> String.concat ", "
        |> sprintf "concat(%s)"
        |> ArmExpression.create

/// A ResourceRef represents a linked resource; typically this will be for two resources that have a relationship
/// such as AppInsights on WebApp. WebApps can automatically create and configure an AI instance for the webapp,
/// or configure the web app to an existing AI instance, or do nothing.
type AutoCreationKind<'T> =
    | Named of ResourceName
    | Derived of ('T -> ResourceName)
    member this.CreateResourceName config =
        match this with
        | Named r -> r
        | Derived f -> f config
type ExternalKind = Managed of ResourceName | Unmanaged of ResourceName
type ResourceRef<'T> =
    | AutoCreate of AutoCreationKind<'T>
    | External of ExternalKind
    member this.CreateResourceName config =
        match this with
        | External (Managed r | Unmanaged r) -> r
        | AutoCreate r -> r.CreateResourceName config
[<AutoOpen>]
module ResourceRef =
    /// Creates a ResourceRef which is automatically created and derived from the supplied config.
    let derived derivation = derivation |> Derived |> AutoCreate
    /// An active pattern that returns the resource name if the resource should be set as a dependency.
    /// In other words, all cases except External Unmanaged.
    let (|DependableResource|_|) config = function
        | External (Managed r) -> Some (DependableResource r)
        | AutoCreate r -> Some(DependableResource(r.CreateResourceName config))
        | External (Unmanaged _) -> None
    /// An active pattern that returns the resource name if the resource should be deployed. In other
    /// words, AutoCreate only.
    let (|DeployableResource|_|) config = function
        | AutoCreate c -> Some (DeployableResource(c.CreateResourceName config))
        | External _ -> None

/// Whether a specific feature is active or not.
type FeatureFlag = Enabled | Disabled member this.AsBoolean = match this with Enabled -> true | Disabled -> false

module FeatureFlag =
    let ofBool enabled = if enabled then Enabled else Disabled

/// Represents an ARM expression that evaluates to a principal ID.
type PrincipalId = PrincipalId of ArmExpression member this.ArmValue = match this with PrincipalId e -> e
type ObjectId = ObjectId of Guid

/// Represents a secret to be captured either via an ARM expression or a secure parameter.
type SecretValue =
    | ParameterSecret of SecureParameter
    | ExpressionSecret of ArmExpression
    member this.Value =
        match this with
        | ParameterSecret secureParameter -> secureParameter.AsArmRef.Eval()
        | ExpressionSecret armExpression -> armExpression.Eval()

type Setting =
    | ParameterSetting of SecureParameter
    | LiteralSetting of string
    | ExpressionSetting of ArmExpression
    member this.Value =
        match this with
        | ParameterSetting secureParameter -> secureParameter.AsArmRef.Eval()
        | LiteralSetting value -> value
        | ExpressionSetting expr -> expr.Eval()
    static member AsLiteral (a,b) = a, LiteralSetting b

type ArmTemplate =
    { Parameters : SecureParameter list
      Outputs : (string * string) list
      Resources : IArmResource list }

type Deployment =
    { Location : Location
      Template : ArmTemplate
      PostDeployTasks : IPostDeploy list }