using Microsoft.UI.Reactor.Controls.Validation;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

public class ValidatorTests
{
    private const string Field = "testField";

    // ════════════════════════════════════════════════════════════════
    //  Required
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Required_Fails_On_Null()
    {
        var v = Validate.Required();
        var result = v.Validate(null, Field);
        Assert.NotNull(result);
        Assert.Equal("REQUIRED", result!.Code);
    }

    [Fact]
    public void Required_Fails_On_Empty_String()
    {
        var v = Validate.Required();
        Assert.NotNull(v.Validate("", Field));
    }

    [Fact]
    public void Required_Fails_On_Whitespace_String()
    {
        var v = Validate.Required();
        Assert.NotNull(v.Validate("   ", Field));
    }

    [Fact]
    public void Required_Passes_On_NonEmpty_String()
    {
        var v = Validate.Required();
        Assert.Null(v.Validate("hello", Field));
    }

    [Fact]
    public void Required_Passes_On_NonNull_Object()
    {
        var v = Validate.Required();
        Assert.Null(v.Validate(42, Field));
    }

    [Fact]
    public void Required_Custom_Message()
    {
        var v = Validate.Required("Please fill this in");
        var result = v.Validate(null, Field);
        Assert.Equal("Please fill this in", result!.Text);
    }

    [Fact]
    public void Required_Sets_Field_From_Parameter()
    {
        var v = Validate.Required();
        var result = v.Validate(null, "email");
        Assert.Equal("email", result!.Field);
    }

    // ════════════════════════════════════════════════════════════════
    //  MinLength
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void MinLength_Fails_When_Too_Short()
    {
        var v = Validate.MinLength(3);
        var result = v.Validate("ab", Field);
        Assert.NotNull(result);
        Assert.Equal("MIN_LENGTH", result!.Code);
    }

    [Fact]
    public void MinLength_Passes_When_Exact()
    {
        var v = Validate.MinLength(3);
        Assert.Null(v.Validate("abc", Field));
    }

    [Fact]
    public void MinLength_Passes_When_Longer()
    {
        var v = Validate.MinLength(3);
        Assert.Null(v.Validate("abcd", Field));
    }

    [Fact]
    public void MinLength_Skips_NonString()
    {
        var v = Validate.MinLength(3);
        Assert.Null(v.Validate(42, Field));
    }

    [Fact]
    public void MinLength_Custom_Message()
    {
        var v = Validate.MinLength(5, "Need 5+ chars");
        var result = v.Validate("abc", Field);
        Assert.Equal("Need 5+ chars", result!.Text);
    }

    // ════════════════════════════════════════════════════════════════
    //  MaxLength
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void MaxLength_Fails_When_Too_Long()
    {
        var v = Validate.MaxLength(3);
        var result = v.Validate("abcd", Field);
        Assert.NotNull(result);
        Assert.Equal("MAX_LENGTH", result!.Code);
    }

    [Fact]
    public void MaxLength_Passes_When_Exact()
    {
        var v = Validate.MaxLength(3);
        Assert.Null(v.Validate("abc", Field));
    }

    [Fact]
    public void MaxLength_Passes_When_Shorter()
    {
        var v = Validate.MaxLength(3);
        Assert.Null(v.Validate("ab", Field));
    }

    [Fact]
    public void MaxLength_Skips_NonString()
    {
        var v = Validate.MaxLength(3);
        Assert.Null(v.Validate(42, Field));
    }

    // ════════════════════════════════════════════════════════════════
    //  Range
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Range_Fails_Below_Min()
    {
        var v = Validate.Range(1, 10);
        var result = v.Validate(0.5, Field);
        Assert.NotNull(result);
        Assert.Equal("RANGE", result!.Code);
    }

    [Fact]
    public void Range_Fails_Above_Max()
    {
        var v = Validate.Range(1, 10);
        Assert.NotNull(v.Validate(11.0, Field));
    }

    [Fact]
    public void Range_Passes_At_Min()
    {
        var v = Validate.Range(1, 10);
        Assert.Null(v.Validate(1.0, Field));
    }

    [Fact]
    public void Range_Passes_At_Max()
    {
        var v = Validate.Range(1, 10);
        Assert.Null(v.Validate(10.0, Field));
    }

    [Fact]
    public void Range_Passes_Within()
    {
        var v = Validate.Range(1, 10);
        Assert.Null(v.Validate(5.0, Field));
    }

    [Fact]
    public void Range_Works_With_Int()
    {
        var v = Validate.Range(0, 100);
        Assert.Null(v.Validate(50, Field));
        Assert.NotNull(v.Validate(-1, Field));
    }

    [Fact]
    public void Range_Works_With_Decimal()
    {
        var v = Validate.Range(0, 100);
        Assert.Null(v.Validate(50m, Field));
    }

    [Fact]
    public void Range_Skips_NonNumeric()
    {
        var v = Validate.Range(0, 100);
        Assert.Null(v.Validate("hello", Field));
    }

    [Fact]
    public void Range_Custom_Message()
    {
        var v = Validate.Range(18, 120, "Invalid age");
        var result = v.Validate(10.0, Field);
        Assert.Equal("Invalid age", result!.Text);
    }

    // ════════════════════════════════════════════════════════════════
    //  Match (regex)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Match_Fails_When_No_Match()
    {
        var v = Validate.Match(@"^\d+$");
        var result = v.Validate("abc", Field);
        Assert.NotNull(result);
        Assert.Equal("MATCH", result!.Code);
    }

    [Fact]
    public void Match_Passes_When_Matches()
    {
        var v = Validate.Match(@"^\d+$");
        Assert.Null(v.Validate("123", Field));
    }

    [Fact]
    public void Match_Skips_Null()
    {
        var v = Validate.Match(@"^\d+$");
        Assert.Null(v.Validate(null, Field));
    }

    [Fact]
    public void Match_Skips_Empty_String()
    {
        // Empty strings are handled by Required, not Match
        var v = Validate.Match(@"^\d+$");
        Assert.Null(v.Validate("", Field));
    }

    [Fact]
    public void Match_Custom_Message()
    {
        var v = Validate.Match(@"^\d{5}$", "Must be 5 digits");
        var result = v.Validate("abc", Field);
        Assert.Equal("Must be 5 digits", result!.Text);
    }

    // ════════════════════════════════════════════════════════════════
    //  Email
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Email_Passes_Valid_Email()
    {
        var v = Validate.Email();
        Assert.Null(v.Validate("user@example.com", Field));
    }

    [Fact]
    public void Email_Passes_Complex_Email()
    {
        var v = Validate.Email();
        Assert.Null(v.Validate("user.name+tag@sub.domain.com", Field));
    }

    [Fact]
    public void Email_Fails_No_At()
    {
        var v = Validate.Email();
        Assert.NotNull(v.Validate("userexample.com", Field));
    }

    [Fact]
    public void Email_Fails_No_Domain()
    {
        var v = Validate.Email();
        Assert.NotNull(v.Validate("user@", Field));
    }

    [Fact]
    public void Email_Skips_Empty()
    {
        var v = Validate.Email();
        Assert.Null(v.Validate("", Field));
    }

    [Fact]
    public void Email_Custom_Message()
    {
        var v = Validate.Email("Bad email");
        var result = v.Validate("bad", Field);
        Assert.Equal("Bad email", result!.Text);
        Assert.Equal("EMAIL", result.Code);
    }

    // ════════════════════════════════════════════════════════════════
    //  Url
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Url_Passes_Http()
    {
        var v = Validate.Url();
        Assert.Null(v.Validate("http://example.com", Field));
    }

    [Fact]
    public void Url_Passes_Https()
    {
        var v = Validate.Url();
        Assert.Null(v.Validate("https://example.com/path?q=1", Field));
    }

    [Fact]
    public void Url_Fails_No_Scheme()
    {
        var v = Validate.Url();
        Assert.NotNull(v.Validate("example.com", Field));
    }

    [Fact]
    public void Url_Fails_Ftp_Scheme()
    {
        var v = Validate.Url();
        Assert.NotNull(v.Validate("ftp://example.com", Field));
    }

    [Fact]
    public void Url_Skips_Empty()
    {
        var v = Validate.Url();
        Assert.Null(v.Validate("", Field));
    }

    [Fact]
    public void Url_Custom_Message()
    {
        var v = Validate.Url("Enter a URL");
        var result = v.Validate("not-a-url", Field);
        Assert.Equal("Enter a URL", result!.Text);
        Assert.Equal("URL", result.Code);
    }

    // ════════════════════════════════════════════════════════════════
    //  Must (custom predicate)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Must_Passes_When_Predicate_True()
    {
        var v = Validate.Must<string>(s => s.Contains("@"), "Need @");
        Assert.Null(v.Validate("a@b", Field));
    }

    [Fact]
    public void Must_Fails_When_Predicate_False()
    {
        var v = Validate.Must<string>(s => s.Contains("@"), "Need @");
        var result = v.Validate("abc", Field);
        Assert.NotNull(result);
        Assert.Equal("Need @", result!.Text);
        Assert.Equal("MUST", result.Code);
    }

    [Fact]
    public void Must_Skips_Wrong_Type()
    {
        var v = Validate.Must<string>(s => s.Length > 0, "Can't be empty");
        Assert.Null(v.Validate(42, Field));
    }

    [Fact]
    public void Must_Complex_Predicate()
    {
        var v = Validate.Must<string>(
            s => s.Any(char.IsUpper) && s.Any(char.IsDigit),
            "Need uppercase and digit");
        Assert.Null(v.Validate("Hello1", Field));
        Assert.NotNull(v.Validate("hello", Field));
        Assert.NotNull(v.Validate("HELLO", Field));
    }

    // ════════════════════════════════════════════════════════════════
    //  MustAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MustAsync_Passes_When_Predicate_True()
    {
        var v = Validate.MustAsync<string>(async s =>
        {
            await Task.Yield();
            return s.Length > 3;
        }, "Too short");
        Assert.Null(await v.ValidateAsync("hello", Field, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MustAsync_Fails_When_Predicate_False()
    {
        var v = Validate.MustAsync<string>(async s =>
        {
            await Task.Yield();
            return s.Length > 3;
        }, "Too short");
        var result = await v.ValidateAsync("ab", Field, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("Too short", result!.Text);
        Assert.Equal("MUST_ASYNC", result.Code);
    }

    [Fact]
    public async Task MustAsync_Respects_Cancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var v = Validate.MustAsync<string>(async s =>
        {
            await Task.Delay(1000);
            return true;
        }, "msg");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            v.ValidateAsync("val", Field, cts.Token));
    }

    // ════════════════════════════════════════════════════════════════
    //  MustBeTrue
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void MustBeTrue_Passes_When_True()
    {
        var v = Validate.MustBeTrue();
        Assert.Null(v.Validate(true, Field));
    }

    [Fact]
    public void MustBeTrue_Fails_When_False()
    {
        var v = Validate.MustBeTrue();
        var result = v.Validate(false, Field);
        Assert.NotNull(result);
        Assert.Equal("MUST_BE_TRUE", result!.Code);
    }

    [Fact]
    public void MustBeTrue_Fails_When_Null()
    {
        var v = Validate.MustBeTrue();
        Assert.NotNull(v.Validate(null, Field));
    }

    [Fact]
    public void MustBeTrue_Custom_Message()
    {
        var v = Validate.MustBeTrue("You must agree to terms");
        var result = v.Validate(false, Field);
        Assert.Equal("You must agree to terms", result!.Text);
    }

    // ════════════════════════════════════════════════════════════════
    //  EqualTo
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void EqualTo_Passes_When_Equal()
    {
        var v = Validate.EqualTo("secret123");
        Assert.Null(v.Validate("secret123", Field));
    }

    [Fact]
    public void EqualTo_Fails_When_Different()
    {
        var v = Validate.EqualTo("secret123");
        var result = v.Validate("secret456", Field);
        Assert.NotNull(result);
        Assert.Equal("EQUAL_TO", result!.Code);
    }

    [Fact]
    public void EqualTo_Works_With_Numbers()
    {
        var v = Validate.EqualTo(42);
        Assert.Null(v.Validate(42, Field));
        Assert.NotNull(v.Validate(43, Field));
    }

    [Fact]
    public void EqualTo_Custom_Message()
    {
        var v = Validate.EqualTo("pw", "Passwords don't match");
        var result = v.Validate("other", Field);
        Assert.Equal("Passwords don't match", result!.Text);
    }

    // ════════════════════════════════════════════════════════════════
    //  Validator composition (chaining)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Chaining_Multiple_Validators_Collects_All_Errors()
    {
        var validators = new IValidator[]
        {
            Validate.Required(),
            Validate.MinLength(3),
            Validate.Match(@"^\d+$", "Digits only")
        };

        // Validate an empty string against all
        var messages = new List<ValidationMessage>();
        foreach (var v in validators)
        {
            var result = v.Validate("", Field);
            if (result is not null) messages.Add(result);
        }

        // Required fails (empty), MinLength skips (not >=3, but empty is length 0 so fails)
        // Match skips (empty string)
        Assert.True(messages.Count >= 1);
        Assert.Contains(messages, m => m.Code == "REQUIRED");
    }

    [Fact]
    public void Chaining_All_Pass()
    {
        var validators = new IValidator[]
        {
            Validate.Required(),
            Validate.MinLength(3),
            Validate.MaxLength(10),
            Validate.Match(@"^[a-zA-Z]+$", "Letters only")
        };

        foreach (var v in validators)
        {
            Assert.Null(v.Validate("Hello", Field));
        }
    }

    [Fact]
    public void Chaining_Partial_Failures()
    {
        var validators = new IValidator[]
        {
            Validate.Required(),
            Validate.MinLength(3),
            Validate.MaxLength(5),
        };

        // "ab" passes Required but fails MinLength
        var messages = validators
            .Select(v => v.Validate("ab", Field))
            .Where(m => m is not null)
            .ToList();

        Assert.Single(messages);
        Assert.Equal("MIN_LENGTH", messages[0]!.Code);
    }
}
