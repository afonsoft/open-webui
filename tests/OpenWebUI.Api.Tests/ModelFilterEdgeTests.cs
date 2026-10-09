using System.Text.RegularExpressions;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes de borda do parse/validação/SSE dos model filters.</summary>
[TestFixture, IsolateEnvironment]
public class ModelFilterEdgeTests
{
    [Test]
    public void Parse_VazioOuInvalido_RetornaListaVazia()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ModelFilterService.Parse(null), Is.Empty);
            Assert.That(ModelFilterService.Parse("   "), Is.Empty);
            Assert.That(ModelFilterService.Parse("{não é json"), Is.Empty);
            Assert.That(ModelFilterService.Parse("{}"), Is.Empty);
            Assert.That(ModelFilterService.Parse("""{"filters": "não-array"}"""), Is.Empty);
            Assert.That(ModelFilterService.Parse("""{"filters": [{"sem": "type"}]}"""), Is.Empty);
        });
    }

    [Test]
    public void Parse_FiltroSemConfig_UsaObjetoVazio()
    {
        var parsed = ModelFilterService.Parse("""{"filters": [{"type": "params_override"}]}""");
        Assert.That(parsed, Has.Count.EqualTo(1));
        Assert.That(parsed[0].Type, Is.EqualTo("params_override"));
    }

    [Test]
    public void Validate_VazioOuSemFilters_RetornaNull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ModelFilterService.Validate(null), Is.Null);
            Assert.That(ModelFilterService.Validate("   "), Is.Null);
            Assert.That(ModelFilterService.Validate("{}"), Is.Null);
        });
    }

    [Test]
    public void Validate_JsonMalformado_ERetornaErro()
    {
        Assert.That(ModelFilterService.Validate("{quebrado"),
            Does.Contain("JSON malformado"));
        Assert.That(ModelFilterService.Validate("""{"filters": 42}"""),
            Does.Contain("deve ser um array"));
        Assert.That(ModelFilterService.Validate("""{"filters": [{"config": {}}]}"""),
            Does.Contain("\"type\" obrigatório"));
        Assert.That(ModelFilterService.Validate("""{"filters": [{"type": "xpto"}]}"""),
            Does.Contain("tipo desconhecido"));
    }

    [Test]
    public void Validate_ConfigObrigatoriaAusente_RetornaErro()
    {
        Assert.That(ModelFilterService.Validate(
                """{"filters": [{"type": "system_inject", "config": {}}]}"""),
            Does.Contain("exige config.text"));
        Assert.That(ModelFilterService.Validate(
                """{"filters": [{"type": "regex_redact", "config": {"pattern": "("}}]}"""),
            Does.Contain("regex inválida"));
        Assert.That(ModelFilterService.Validate(
                """{"filters": [{"type": "regex_redact", "config": {}}]}"""),
            Does.Contain("exige config.pattern"));
        Assert.That(ModelFilterService.Validate(
                """{"filters": [{"type": "max_tokens_cap", "config": {"max_tokens": 0}}]}"""),
            Does.Contain("exige config.max_tokens"));
    }

    [Test]
    public void Validate_ConfigsValidas_RetornaNull()
    {
        Assert.That(ModelFilterService.Validate("""
            {"filters": [
                {"type": "system_inject", "config": {"text": "oi"}},
                {"type": "regex_redact", "config": {"pattern": "a+b"}},
                {"type": "max_tokens_cap", "config": {"max_tokens": 100}},
                {"type": "params_override", "config": {}}
            ]}
            """), Is.Null);
    }

    [Test]
    public void ProcessSseLine_SemRegrasOuNaoData_PassaIntacto()
    {
        var rules = new List<(Regex, string)> { (new Regex("senha"), "***") };
        Assert.Multiple(() =>
        {
            Assert.That(ModelFilterService.ProcessSseLine("event: ping", rules),
                Is.EqualTo("event: ping"));
            Assert.That(ModelFilterService.ProcessSseLine("data: [DONE]", rules),
                Is.EqualTo("data: [DONE]"));
            Assert.That(ModelFilterService.ProcessSseLine("data: {quebrado", rules),
                Is.EqualTo("data: {quebrado"));
            Assert.That(ModelFilterService.ProcessSseLine(
                "data: {\"x\":1}", []), Is.EqualTo("data: {\"x\":1}"));
        });
    }

    [Test]
    public void ProcessSseLine_RedigeDeltaEMessage()
    {
        var rules = new List<(Regex, string)> { (new Regex("segredo", RegexOptions.IgnoreCase), "***") };

        var delta = ModelFilterService.ProcessSseLine(
            """data: {"choices":[{"delta":{"content":"meu SEGREDO aqui"}}]}""", rules);
        var message = ModelFilterService.ProcessSseLine(
            """data: {"choices":[{"message":{"content":"meu segredo aqui"}}]}""", rules);
        var intacto = ModelFilterService.ProcessSseLine(
            """data: {"choices":[{"delta":{"content":"nada aqui"}}]}""", rules);

        Assert.Multiple(() =>
        {
            Assert.That(delta, Does.Contain("***"));
            Assert.That(delta, Does.Not.Contain("SEGREDO"));
            Assert.That(message, Does.Contain("***"));
            Assert.That(intacto, Is.EqualTo("""data: {"choices":[{"delta":{"content":"nada aqui"}}]}"""));
        });
    }

    [Test]
    public void OutletRules_StageInlet_EhIgnorado()
    {
        var filters = ModelFilterService.Parse("""
            {"filters": [
                {"type": "regex_redact", "config": {"pattern": "x", "stage": "inlet"}},
                {"type": "regex_redact", "config": {"pattern": "y", "stage": "outlet"}},
                {"type": "regex_redact", "config": {"pattern": "z"}}
            ]}
            """);
        var rules = ModelFilterService.OutletRules(filters);
        Assert.That(rules, Has.Count.EqualTo(2));
    }
}
