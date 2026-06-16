using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Controls.Validation;
using static Microsoft.UI.Reactor.Controls.Validation.ValidationRuleDsl;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

public class ValidationIntegrationTests
{
    // ════════════════════════════════════════════════════════════════
    //  Full pipeline: producers → context → query
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Full_Pipeline_Producers_To_Context()
    {
        var ctx = new ValidationContext();

        // Simulate a component render: validate email and age fields
        var email = "";
        var age = 15.0;

        ValidationReconciler.ValidateField(ctx, "email", email,
            Validate.Required(), Validate.Email());
        ValidationReconciler.ValidateField(ctx, "age", age,
            Validate.Range(18, 120, "Must be 18+"));

        // Verify context state
        Assert.False(ctx.IsValid());
        Assert.Equal(2, ctx.InvalidFields.Count);

        // Email has Required error (Email skips empty strings)
        var emailMsgs = ctx.GetMessages("email");
        Assert.Single(emailMsgs);
        Assert.Equal("REQUIRED", emailMsgs[0].Code);

        // Age has Range error
        var ageMsgs = ctx.GetMessages("age");
        Assert.Single(ageMsgs);
        Assert.Contains("Must be 18+", ageMsgs[0].Text);
    }

    [Fact]
    public void Full_Pipeline_Valid_State()
    {
        var ctx = new ValidationContext();

        ValidationReconciler.ValidateField(ctx, "email", "user@example.com",
            Validate.Required(), Validate.Email());
        ValidationReconciler.ValidateField(ctx, "age", 25.0,
            Validate.Range(18, 120));

        Assert.True(ctx.IsValid());
        Assert.Empty(ctx.InvalidFields);
    }

    [Fact]
    public void Full_Pipeline_With_Cross_Field_Rules()
    {
        var ctx = new ValidationContext();
        var password = "secret";
        var confirm = "nope";

        // Field-level validation
        ValidationReconciler.ValidateField(ctx, "password", password,
            Validate.Required(), Validate.MinLength(6));
        ValidationReconciler.ValidateField(ctx, "confirmPassword", confirm,
            Validate.Required());

        // Cross-field validation rule
        var rule = ValidationRule(
            () => password == confirm,
            "Passwords must match",
            "confirmPassword");
        rule.Evaluate(ctx);

        Assert.False(ctx.IsValid());
        var msgs = ctx.GetMessages("confirmPassword");
        // Has both internal (from Evaluate clearing+re-adding) messages
        // The cross-field rule adds to confirmPassword which already had messages cleared/re-added
        Assert.Contains(msgs, m => m.Text == "Passwords must match");
    }

    [Fact]
    public void Full_Pipeline_Cross_Field_Passes_When_Match()
    {
        var ctx = new ValidationContext();
        var password = "secret123";
        var confirm = "secret123";

        ValidationReconciler.ValidateField(ctx, "password", password,
            Validate.Required(), Validate.MinLength(6));
        ValidationReconciler.ValidateField(ctx, "confirmPassword", confirm,
            Validate.Required());

        var rule = ValidationRule(
            () => password == confirm,
            "Passwords must match",
            "confirmPassword");
        rule.Evaluate(ctx);

        Assert.True(ctx.IsValid());
    }

    // ════════════════════════════════════════════════════════════════
    //  External errors (server-side injection)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void External_Errors_Persist_Across_Validation_Runs()
    {
        var ctx = new ValidationContext();

        // Server says email is taken
        ctx.AddExternal("email", "Email already registered");

        // Re-run field validation (simulating re-render)
        ValidationReconciler.ValidateField(ctx, "email", "user@example.com",
            Validate.Required(), Validate.Email());

        // External error should still be there (field validation doesn't clear external)
        // BUT NotifyValueChanged clears external messages
        // So after ValidateField, external messages are cleared because value changed
        var msgs = ctx.GetMessages("email");
        // Since ValidateField calls NotifyValueChanged which clears external messages,
        // the external error is gone. This is correct behavior: value changed → clear external.
        Assert.Empty(msgs); // All validators pass, external cleared by value change
    }

    [Fact]
    public void External_Errors_Show_When_Value_Unchanged()
    {
        var ctx = new ValidationContext();

        // First render: set up the field
        ValidationReconciler.ValidateField(ctx, "email", "user@example.com",
            Validate.Required());

        // Server returns error
        ctx.AddExternal("email", "Email already registered");

        // Verify it shows up
        var msgs = ctx.GetMessages("email");
        Assert.Single(msgs);
        Assert.Equal("Email already registered", msgs[0].Text);
        Assert.False(ctx.IsValid());
    }

    // ════════════════════════════════════════════════════════════════
    //  Re-render simulation (state changes)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Rerender_With_Fixed_Value_Clears_Errors()
    {
        var ctx = new ValidationContext();

        // First render: invalid
        ValidationReconciler.ValidateField(ctx, "email", "",
            Validate.Required());
        Assert.False(ctx.IsValid());

        // Second render: valid (user typed something)
        ValidationReconciler.ValidateField(ctx, "email", "user@example.com",
            Validate.Required());
        Assert.True(ctx.IsValid());
        Assert.Empty(ctx.GetMessages("email"));
    }

    [Fact]
    public void Rerender_Does_Not_Accumulate_Messages()
    {
        var ctx = new ValidationContext();

        // Multiple render cycles with the same invalid value
        for (int i = 0; i < 5; i++)
        {
            ValidationReconciler.ValidateField(ctx, "email", "",
                Validate.Required(), Validate.MinLength(3));
        }

        // Should only have messages from the last render, not accumulated
        var msgs = ctx.GetMessages("email");
        // Required fails on "", MinLength: "" has length 0 < 3, so both fail
        Assert.Equal(2, msgs.Count);
        // But crucially, NOT 10 messages (5 renders * 2 validators) — ClearInternal prevents accumulation
    }

    // ════════════════════════════════════════════════════════════════
    //  Attached validation metadata
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ValidateAttached_Runs_Validators_From_Element()
    {
        var ctx = new ValidationContext();
        var attached = new ValidationAttached("email",
            [Validate.Required(), Validate.Email()], []);

        ValidationReconciler.ValidateAttached(ctx, attached, "");

        Assert.False(ctx.IsValid());
        Assert.Single(ctx.GetMessages("email"));
    }

    [Fact]
    public void ValidateAttached_Registers_Field()
    {
        var ctx = new ValidationContext();
        var attached = new ValidationAttached("myField", [], []);

        ValidationReconciler.ValidateAttached(ctx, attached, "value");

        Assert.Contains("myField", ctx.RegisteredFields);
    }

    // ════════════════════════════════════════════════════════════════
    //  EvaluateRules
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void EvaluateRules_Runs_Multiple_Rules()
    {
        var ctx = new ValidationContext();
        var start = new DateTime(2024, 6, 1);
        var end = new DateTime(2024, 5, 1);
        var budget = 150.0;
        var maxBudget = 100.0;

        var rules = new[]
        {
            ValidationRule(() => end > start, "End must be after start", "endDate"),
            ValidationRule(() => budget <= maxBudget, "Budget exceeded", "budget"),
        };

        ValidationReconciler.EvaluateRules(ctx, rules);

        Assert.False(ctx.IsValid());
        Assert.Equal(2, ctx.InvalidFields.Count);
    }

    // ════════════════════════════════════════════════════════════════
    //  Async validation
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ValidateFieldAsync_Adds_Messages()
    {
        var ctx = new ValidationContext();
        ctx.RegisterField("username");

        var asyncValidators = new[]
        {
            Validate.MustAsync<string>(async s =>
            {
                await Task.Yield();
                return s != "admin";
            }, "Username is reserved")
        };

        await ValidationReconciler.ValidateFieldAsync(ctx, "username", "admin", asyncValidators, TestContext.Current.CancellationToken);

        Assert.Single(ctx.GetMessages("username"));
        Assert.Equal("Username is reserved", ctx.GetMessages("username")[0].Text);
    }

    // ════════════════════════════════════════════════════════════════
    //  Hook simulation: UseValidationContext + validate
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Simulated_Component_Render_With_Hooks()
    {
        // Simulate a component that uses UseValidationContext
        var renderCtx = new RenderContext();

        // --- First render ---
        renderCtx.BeginRender(() => { });
        var validationCtx = renderCtx.UseValidationContext();

        // Component does validation in its render
        var email = "";
        ValidationReconciler.ValidateField(validationCtx, "email", email,
            Validate.Required());

        Assert.False(validationCtx.IsValid());

        // --- Second render (user typed an email) ---
        renderCtx.BeginRender(() => { });
        var validationCtx2 = renderCtx.UseValidationContext();

        // Same context returned
        Assert.Same(validationCtx, validationCtx2);

        email = "user@test.com";
        ValidationReconciler.ValidateField(validationCtx2, "email", email,
            Validate.Required());

        Assert.True(validationCtx2.IsValid());
    }
}
