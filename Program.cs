using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<ContactStore>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});

app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/openapi.json", (IWebHostEnvironment environment) =>
{
    var path = Path.Combine(environment.ContentRootPath, "openapi.json");
    return Results.File(path, "application/json");
});
app.MapGet("/swagger", () => Results.Content(SwaggerPage.Html, "text/html"));

var api = app.MapGroup("/api/v1/contacts");

api.MapGet("/", (ContactStore store, string? search, string? sortBy, string? order,
    bool? favorite, int? page, int? pageSize) =>
{
    var field = (sortBy ?? "name").ToLowerInvariant();
    if (field is not ("name" or "createdat" or "updatedat"))
        return Results.Problem(statusCode: 400, title: "Invalid sortBy",
            detail: "Use name, createdAt, or updatedAt.");
    var direction = (order ?? "asc").ToLowerInvariant();
    if (direction is not ("asc" or "desc"))
        return Results.Problem(statusCode: 400, title: "Invalid order",
            detail: "Use asc or desc.");
    var pageNumber = page ?? 1;
    var size = pageSize ?? 20;
    if (pageNumber < 1 || size is < 1 or > 100)
        return Results.Problem(statusCode: 400, title: "Invalid pagination",
            detail: "page must be at least 1; pageSize must be from 1 to 100.");

    var all = store.Snapshot().Where(contact =>
        (favorite is null || contact.IsFavorite == favorite) &&
        (string.IsNullOrWhiteSpace(search) ||
         new[] { contact.Name, contact.Email, contact.Phone, contact.Address.City }
             .Any(value => value.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))));
    var sorted = field switch
    {
        "createdat" => direction == "desc"
            ? all.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            : all.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id),
        "updatedat" => direction == "desc"
            ? all.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id)
            : all.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id),
        _ => direction == "desc"
            ? all.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id)
            : all.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id)
    };
    var materialized = sorted.ToArray();
    return Results.Ok(new ContactPage(
        materialized.Skip((int)Math.Min((pageNumber - 1L) * size, int.MaxValue)).Take(size).ToArray(),
        materialized.Length, pageNumber, size));
});

api.MapGet("/{id:guid}", (Guid id, ContactStore store) =>
    store.Find(id) is { } contact ? Results.Ok(contact) : NotFound());

api.MapPost("/", (ContactInput? input, ContactStore store) =>
{
    var errors = Validation.Check(input);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    var created = store.Create(input!);
    return Results.Created($"/api/v1/contacts/{created.Id}", created);
});

api.MapPut("/{id:guid}", (Guid id, ContactInput? input, ContactStore store) =>
{
    var errors = Validation.Check(input);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    var updated = store.Replace(id, input!);
    return updated is null ? NotFound() : Results.Ok(updated);
});

api.MapPatch("/{id:guid}/favorite", (Guid id, FavoriteInput? input, ContactStore store) =>
{
    if (input?.IsFavorite is null) return Results.ValidationProblem(
        new Dictionary<string, string[]> { ["isFavorite"] = ["A boolean value is required."] });
    var updated = store.SetFavorite(id, input.IsFavorite.Value);
    return updated is null ? NotFound() : Results.Ok(updated);
});

api.MapDelete("/{id:guid}", (Guid id, ContactStore store) =>
    store.Delete(id) ? Results.NoContent() : NotFound());

app.Run();

static IResult NotFound() => Results.Problem(statusCode: 404, title: "Contact not found");

public partial class Program { }

public sealed record Address(string Street, string City, string Country, string? PostalCode);
public sealed record Contact(Guid Id, string Name, string Email, string Phone, Address Address,
    string? Notes, bool IsFavorite, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ContactInput(string Name, string Email, string Phone, Address Address,
    string? Notes, bool IsFavorite);
public sealed record FavoriteInput(bool? IsFavorite);
public sealed record ContactPage(Contact[] Items, int Total, int Page, int PageSize);

public sealed class ContactStore
{
    private readonly Dictionary<Guid, Contact> _contacts = [];
    private readonly object _gate = new();

    public Contact[] Snapshot() { lock (_gate) return _contacts.Values.ToArray(); }
    public Contact? Find(Guid id) { lock (_gate) return _contacts.GetValueOrDefault(id); }
    public Contact Create(ContactInput input)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new Contact(Guid.NewGuid(), input.Name.Trim(), input.Email.Trim(),
            input.Phone.Trim(), Clean(input.Address), input.Notes?.Trim(), input.IsFavorite, now, now);
        lock (_gate) _contacts.Add(item.Id, item);
        return item;
    }
    public Contact? Replace(Guid id, ContactInput input)
    {
        lock (_gate)
        {
            if (!_contacts.TryGetValue(id, out var previous)) return null;
            var cleanedAddress = Clean(input.Address);
            var cleanedNotes = input.Notes?.Trim();
            if (previous.Name == input.Name.Trim() && previous.Email == input.Email.Trim() &&
                previous.Phone == input.Phone.Trim() && previous.Address == cleanedAddress &&
                previous.Notes == cleanedNotes && previous.IsFavorite == input.IsFavorite)
                return previous;
            var item = previous with
            {
                Name = input.Name.Trim(), Email = input.Email.Trim(), Phone = input.Phone.Trim(),
                Address = cleanedAddress, Notes = cleanedNotes,
                IsFavorite = input.IsFavorite, UpdatedAt = DateTimeOffset.UtcNow
            };
            _contacts[id] = item;
            return item;
        }
    }
    public Contact? SetFavorite(Guid id, bool value)
    {
        lock (_gate)
        {
            if (!_contacts.TryGetValue(id, out var previous)) return null;
            if (previous.IsFavorite == value) return previous;
            var item = previous with { IsFavorite = value, UpdatedAt = DateTimeOffset.UtcNow };
            _contacts[id] = item;
            return item;
        }
    }
    public bool Delete(Guid id) { lock (_gate) return _contacts.Remove(id); }
    private static Address Clean(Address value) => new(value.Street.Trim(), value.City.Trim(),
        value.Country.Trim(), value.PostalCode?.Trim());
}

public static class Validation
{
    public static Dictionary<string, string[]> Check(ContactInput? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) { errors["body"] = ["A JSON body is required."]; return errors; }
        Required(input.Name, "name", 100);
        if (string.IsNullOrWhiteSpace(input.Email) || input.Email.Length > 254 ||
            !new EmailAddressAttribute().IsValid(input.Email))
            errors["email"] = ["Provide a valid email address of at most 254 characters."];
        Required(input.Phone, "phone", 40);
        if (input.Address is null) errors["address"] = ["Address is required."];
        else
        {
            Required(input.Address.Street, "address.street", 200);
            Required(input.Address.City, "address.city", 100);
            Required(input.Address.Country, "address.country", 100);
            if (input.Address.PostalCode?.Length > 20)
                errors["address.postalCode"] = ["Use at most 20 characters."];
        }
        if (input.Notes?.Length > 1000) errors["notes"] = ["Use at most 1000 characters."];
        return errors;

        void Required(string? value, string field, int max)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max)
                errors[field] = [$"Required; use at most {max} characters."];
        }
    }
}

public static class SwaggerPage
{
    public const string Html = """
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Address Book API | Swagger</title>
        <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5/swagger-ui.css">
        </head><body><div id="swagger-ui"></div>
        <script src="https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
        <script>window.onload=()=>SwaggerUIBundle({url:'/openapi.json',dom_id:'#swagger-ui',deepLinking:true});</script>
        </body></html>
        """;
}
