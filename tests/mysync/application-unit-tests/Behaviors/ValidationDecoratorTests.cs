using FluentValidation;
using MySync.Application.Abstractions.Behaviors;
using MySync.Application.Abstractions.Messaging;
using NSubstitute;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Behaviors;

public class ValidationDecoratorTests
{
    [Fact]
    public async Task Handle_Should_ShortCircuit_WhenValidationFails()
    {
        ICommandHandler<SampleCommand> inner = Substitute.For<ICommandHandler<SampleCommand>>();
        var decorator = new ValidationDecorator.CommandBaseHandler<SampleCommand>(inner, [new SampleCommandValidator()]);

        Result result = await decorator.Handle(new SampleCommand(""), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<ValidationError>();
        result.Error.Type.ShouldBe(ErrorType.Validation);
        await inner.DidNotReceiveWithAnyArgs().Handle(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Handle_Should_CallTheInnerHandler_WhenValidationPasses()
    {
        ICommandHandler<SampleCommand> inner = Substitute.For<ICommandHandler<SampleCommand>>();
        inner.Handle(Arg.Any<SampleCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var decorator = new ValidationDecorator.CommandBaseHandler<SampleCommand>(inner, [new SampleCommandValidator()]);

        Result result = await decorator.Handle(new SampleCommand("value"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await inner.Received(1).Handle(Arg.Any<SampleCommand>(), Arg.Any<CancellationToken>());
    }

    public sealed record SampleCommand(string Value) : ICommand;

    private sealed class SampleCommandValidator : AbstractValidator<SampleCommand>
    {
        public SampleCommandValidator() => RuleFor(command => command.Value).NotEmpty();
    }
}
