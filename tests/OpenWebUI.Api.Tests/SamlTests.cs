using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da slice enterprise-sso (SAML 2.0): metadata do SP, redirect
/// SP-initiated ao IdP e ACS validando assertions assinadas (fixture com
/// certificado self-signed gerado em teste) — JIT user + JWT + 401 quando
/// a assinatura/issuer/audiência não confere.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class SamlTests
{
    private const string IdpEntityId = "https://idp.test/entity";
    private const string AcsAudience = "http://localhost/saml/acs";

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _anon = null!;
    private string _dbPath = null!;
    private X509Certificate2 _idpCert = null!;
    private X509Certificate2 _rogueCert = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-saml-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _admin = _factory.CreateClient();
        _anon = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var admin = await SignUpAsync("Admin", "admin@saml.local", "senha123");
        _admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", admin.Token);
        await _admin.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });

        _idpCert = NewCert("cn=idp-test", IdpRsaKey);
        _rogueCert = NewCert("cn=rogue", RogueRsaKey);

        var cfg = await _admin.PostAsJsonAsync("/api/v1/configs/saml",
            new SamlConfigRequest(
                Enabled: true,
                IdpSsoUrl: "https://idp.test/sso",
                IdpEntityId: IdpEntityId,
                IdpCert: _idpCert.ExportCertificatePem(),
                SpEntityId: null,
                RoleAttribute: null));
        cfg.EnsureSuccessStatusCode();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _idpCert.Dispose();
        _rogueCert.Dispose();
        IdpRsaKey.Dispose();
        RogueRsaKey.Dispose();
        _anon.Dispose();
        _admin.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private async Task<(string Token, string UserId)> SignUpAsync(
        string name, string email, string password)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email, password });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return (auth!.Token, auth.User.Id);
    }

    // Cada certificado precisa da própria chave: compartilhar uma RSA faz o
    // cert "rogue" assinar com a mesma chave do IdP — a assinatura passa na
    // validação e T04 deixa de testar o que deveria.
    private static readonly RSA IdpRsaKey = RSA.Create(2048);
    private static readonly RSA RogueRsaKey = RSA.Create(2048);

    private static X509Certificate2 NewCert(string cn, RSA key) =>
        new CertificateRequest(cn, key, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(1));

    /// <summary>GET /saml/metadata retorna EntityDescriptor do SP.</summary>
    [Test]
    public async Task T01_Metadata_RetornaEntityDescriptor()
    {
        var response = await _anon.GetAsync("/saml/metadata");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var xml = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(xml, Does.Contain("EntityDescriptor"));
            Assert.That(xml, Does.Contain("/saml/acs"));
            Assert.That(xml, Does.Contain("WantAssertionsSigned=\"true\""));
        });
    }

    /// <summary>GET /saml/login redireciona ao IdP com SAMLRequest encodado.</summary>
    [Test]
    public async Task T02_Login_RedirecionaAoIdp()
    {
        var response = await _anon.GetAsync("/saml/login");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var location = response.Headers.Location!.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(location, Does.StartWith("https://idp.test/sso"));
            Assert.That(location, Does.Contain("SAMLRequest="));
        });
    }

    /// <summary>Assertion assinada pelo IdP autentica e emite JWT (JIT user).</summary>
    [Test]
    public async Task T03_Acs_AssertionValida_CriaUsuarioEEmeiteJwt()
    {
        var samlResponse = BuildSignedResponse(
            _idpCert, email: "samluser@saml.local", nameId: "samluser@saml.local",
            audience: AcsAudience);
        var response = await PostAcsAsync(samlResponse);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var location = response.Headers.Location!.ToString();
        Assert.That(location, Does.StartWith("/?oauth_token="));

        var token = Uri.UnescapeDataString(location["/?oauth_token=".Length..]);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        var me = await client.GetFromJsonAsync<UserResponse>("/api/v1/auths/");
        Assert.That(me!.Email, Is.EqualTo("samluser@saml.local"));
    }

    /// <summary>Assertion assinada por certificado desconhecido é rejeitada.</summary>
    [Test]
    public async Task T04_Acs_AssinaturaInvalida_Retorna401()
    {
        var samlResponse = BuildSignedResponse(
            _rogueCert, email: "rogue@saml.local", nameId: "rogue@saml.local",
            audience: AcsAudience);
        var response = await PostAcsAsync(samlResponse);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>Assertion com issuer divergente do configurado é rejeitada.</summary>
    [Test]
    public async Task T05_Acs_IssuerDivergente_Retorna401()
    {
        var samlResponse = BuildSignedResponse(
            _idpCert, email: "x@saml.local", nameId: "x@saml.local",
            audience: AcsAudience, issuer: "https://outro-idp.test/entity");
        var response = await PostAcsAsync(samlResponse);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>Assertion com audience incorreta é rejeitada.</summary>
    [Test]
    public async Task T06_Acs_AudienceIncorreta_Retorna401()
    {
        var samlResponse = BuildSignedResponse(
            _idpCert, email: "x@saml.local", nameId: "x@saml.local",
            audience: "https://outra-app.test/acs");
        var response = await PostAcsAsync(samlResponse);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>SAML desabilitado → endpoints retornam 404.</summary>
    [Test]
    public async Task T07_Desabilitado_EndpointsRetornam404()
    {
        await _admin.PostAsJsonAsync("/api/v1/configs/saml",
            new SamlConfigRequest(false, null, null, null, null, null));
        try
        {
            Assert.Multiple(async () =>
            {
                Assert.That((await _anon.GetAsync("/saml/login")).StatusCode,
                    Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That((await _anon.GetAsync("/saml/metadata")).StatusCode,
                    Is.EqualTo(HttpStatusCode.NotFound));
            });
        }
        finally
        {
            await _admin.PostAsJsonAsync("/api/v1/configs/saml",
                new SamlConfigRequest(true, null, null, null, null, null));
        }
    }

    private async Task<HttpResponseMessage> PostAcsAsync(string samlResponseBase64)
    {
        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["SAMLResponse"] = samlResponseBase64 });
        return await _anon.PostAsync("/saml/acs", content);
    }

    /// <summary>Monta uma SAMLResponse com Assertion assinada (enveloped signature).</summary>
    private static string BuildSignedResponse(
        X509Certificate2 cert, string email, string nameId,
        string audience, string? issuer = null)
    {
        issuer ??= IdpEntityId;
        var now = DateTime.UtcNow;
        var assertionId = $"_a{Guid.NewGuid():N}";
        var xml = $"""
            <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
                xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion"
                ID="_r{Guid.NewGuid():N}" Version="2.0"
                IssueInstant="{now:yyyy-MM-dd'T'HH:mm:ss'Z'}"
                Destination="http://localhost/saml/acs">
              <saml:Issuer>{issuer}</saml:Issuer>
              <saml:Assertion ID="{assertionId}" Version="2.0"
                  IssueInstant="{now:yyyy-MM-dd'T'HH:mm:ss'Z'}">
                <saml:Issuer>{issuer}</saml:Issuer>
                <saml:Subject>
                  <saml:NameID Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress">{nameId}</saml:NameID>
                </saml:Subject>
                <saml:Conditions
                    NotBefore="{now.AddMinutes(-5):yyyy-MM-dd'T'HH:mm:ss'Z'}"
                    NotOnOrAfter="{now.AddMinutes(5):yyyy-MM-dd'T'HH:mm:ss'Z'}">
                  <saml:AudienceRestriction>
                    <saml:Audience>{audience}</saml:Audience>
                  </saml:AudienceRestriction>
                </saml:Conditions>
                <saml:AttributeStatement>
                  <saml:Attribute Name="email">
                    <saml:AttributeValue>{email}</saml:AttributeValue>
                  </saml:Attribute>
                  <saml:Attribute Name="name">
                    <saml:AttributeValue>Usuário SAML</saml:AttributeValue>
                  </saml:Attribute>
                </saml:AttributeStatement>
              </saml:Assertion>
            </samlp:Response>
            """;

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        var assertion = (XmlElement)doc.GetElementsByTagName(
            "Assertion", "urn:oasis:names:tc:SAML:2.0:assertion")[0]!;

        var signedXml = new SignedXml(assertion)
        {
            SigningKey = cert.GetRSAPrivateKey(),
        };
        signedXml.SignedInfo!.CanonicalizationMethod =
            SignedXml.XmlDsigExcC14NTransformUrl;
        var reference = new Reference($"#{assertion.GetAttribute("ID")}");
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(cert));
        signedXml.KeyInfo = keyInfo;
        signedXml.ComputeSignature();
        assertion.AppendChild(doc.ImportNode(signedXml.GetXml(), true));

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml));
    }
}
