using Custodex.Abstractions;
using Custodex.AspNetCore;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace Custodex.Blazor;

/// <summary>
/// Authorizes its content against a Custodex permission on a specific object. A per-object wrapper
/// over <see cref="AuthorizeView"/>: it computes the <c>{prefix}:{type}:{permission}</c> policy and
/// passes the object as the authorization resource, so it shares the engine evaluation path.
/// </summary>
public partial class CustodexAuthorizeView
{
    [Inject]
    private IOptions<CustodexAuthorizationOptions> Options { get; set; } = default!;

    /// <summary>The object to authorize, as an <see cref="EntityRef"/>. Takes precedence over <see cref="ObjectType"/> and <see cref="ObjectId"/>.</summary>
    [Parameter]
    public EntityRef? Object { get; set; }

    /// <summary>The object's entity type, used with <see cref="ObjectId"/> when <see cref="Object"/> is not set.</summary>
    [Parameter]
    public string? ObjectType { get; set; }

    /// <summary>The object's id, used with <see cref="ObjectType"/> when <see cref="Object"/> is not set.</summary>
    [Parameter]
    public string? ObjectId { get; set; }

    /// <summary>The permission to check on the object.</summary>
    [Parameter]
    [EditorRequired]
    public string Permission { get; set; } = default!;

    /// <summary>Content shown when the subject is authorized.</summary>
    [Parameter]
    public RenderFragment<AuthenticationState>? Authorized { get; set; }

    /// <summary>Content shown when the subject is not authorized.</summary>
    [Parameter]
    public RenderFragment<AuthenticationState>? NotAuthorized { get; set; }

    /// <summary>Content shown while authorization is in progress.</summary>
    [Parameter]
    public RenderFragment? Authorizing { get; set; }

    /// <summary>Default content, shown when the subject is authorized.</summary>
    [Parameter]
    public RenderFragment<AuthenticationState>? ChildContent { get; set; }

    private EntityRef ResolvedObject => Object ?? new EntityRef(
        ObjectType ?? throw new InvalidOperationException("CustodexAuthorizeView requires Object or both ObjectType and ObjectId."),
        ObjectId ?? throw new InvalidOperationException("CustodexAuthorizeView requires Object or both ObjectType and ObjectId."));

    private object ResolvedObjectBox => ResolvedObject;

    private string PolicyName
    {
        get
        {
            var o = Options.Value;
            var entity = ResolvedObject;
            return $"{o.PolicyPrefix}{o.PolicySeparator}{entity.Type}{o.PolicySeparator}{Permission}";
        }
    }
}
