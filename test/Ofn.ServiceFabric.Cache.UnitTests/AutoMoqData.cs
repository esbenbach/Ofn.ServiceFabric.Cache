namespace Ofn.ServiceFabric.Cache.UnitTests;

using AutoFixture;
using AutoFixture.AutoMoq;
using AutoFixture.Xunit3;
using Moq;
using System.Fabric;
using System.Numerics;

public class AutoMoqDataAttribute : AutoDataAttribute
{
    public AutoMoqDataAttribute() : base(() =>
    {
        var fixture = new Fixture();
        fixture.Customize(new AutoMoqCustomization { GenerateDelegates = true });
        fixture.Customizations.Insert(0, new AutoFixture.Kernel.TypeRelay(typeof(TimeProvider), typeof(FakeTimeProvider)));
        fixture.Register(CreateStatefulServiceContext);
        return fixture;
    })
    {
    }

    // SF SDK 8.4+ calls telemetry in StatefulServiceBase..ctor that accesses
    // CodePackageActivationContext string properties — they must be non-null.
    private static StatefulServiceContext CreateStatefulServiceContext()
    {
        var codePackageActivationContext = new Mock<ICodePackageActivationContext>();
        codePackageActivationContext.SetupGet(c => c.ApplicationName).Returns("fabric:/TestApp");
        codePackageActivationContext.SetupGet(c => c.ApplicationTypeName).Returns("TestAppType");

        var nodeContext = new NodeContext(
            "Node0",
            new NodeId(BigInteger.One, BigInteger.One),
            BigInteger.Zero,
            "NodeType0",
            "localhost");

        return new StatefulServiceContext(
            nodeContext,
            codePackageActivationContext.Object,
            "TestServiceType",
            new Uri("fabric:/TestApp/TestService"),
            null,
            Guid.NewGuid(),
            0L);
    }
}
