using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Architecture.Tests;

public sealed class IdentityAccessArchitectureTests
{
    private const string AspNetCoreIdentity = "Microsoft.AspNetCore.Identity";

    private static readonly string[] ForbiddenFrameworks =
        ["Microsoft.AspNetCore", "Microsoft.Extensions.Identity", "Dapper", "Npgsql"];

    [Fact]
    public void Domain_has_no_web_identity_or_data_access_dependency()
    {
        var result = Types.InAssembly(Assembly.Load("SocAlytics.Platform.Domain"))
            .ShouldNot()
            .HaveDependencyOnAny(ForbiddenFrameworks)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue("The Domain layer must not depend on ASP.NET Core, Identity, Dapper, or Npgsql.");
    }

    [Fact]
    public void Application_has_no_web_identity_or_data_access_dependency()
    {
        var result = Types.InAssembly(typeof(ApplicationServiceCollectionExtensions).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(ForbiddenFrameworks)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue("The Application layer must not depend on ASP.NET Core, Identity, Dapper, or Npgsql.");
    }

    [Fact]
    public void Infrastructure_public_types_do_not_expose_aspnet_identity()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in typeof(InfrastructureServiceCollectionExtensions).Assembly.GetExportedTypes())
        {
            IsIdentityType(type.BaseType).ShouldBeFalse($"{type} derives from an ASP.NET Core Identity type.");
            type.GetInterfaces().Any(IsIdentityType).ShouldBeFalse($"{type} implements an ASP.NET Core Identity type.");

            foreach (var member in type.GetMembers(all))
            {
                var exposed = member switch
                {
                    MethodBase method when !method.IsPrivate => method.GetParameters().Select(p => p.ParameterType)
                        .Append((method as MethodInfo)?.ReturnType),
                    PropertyInfo property => [property.PropertyType],
                    FieldInfo { IsPrivate: false } field => [field.FieldType],
                    _ => [],
                };

                exposed.Any(IsIdentityType).ShouldBeFalse($"{type}.{member.Name} exposes an ASP.NET Core Identity type.");
            }
        }
    }

    private static bool IsIdentityType(Type? type)
    {
        while (type is { HasElementType: true })
        {
            type = type.GetElementType();
        }

        if (type is null)
        {
            return false;
        }

        return (type.Namespace?.StartsWith(AspNetCoreIdentity, StringComparison.Ordinal) ?? false)
            || (type.IsGenericType && type.GetGenericArguments().Any(IsIdentityType));
    }
}
