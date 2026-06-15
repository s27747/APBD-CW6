# APBD-CW6

Projekt ASP.NET Core Web API obsługujący wizyty w przychodni. Aplikacja używa ADO.NET oraz Microsoft.Data.SqlClient.

## Technologie

- .NET 8
- ASP.NET Core Web API
- ADO.NET
- Microsoft SQL Server

## Uruchomienie bazy danych

W SQL Server Management Studio albo DataGrip uruchom skrypt:

```sql
sql/01_create_and_seed_clinic.sql
```

Skrypt tworzy bazę `ClinicAdoNet`, tabele oraz przykładowe dane.

## Connection string

Connection string znajduje się w pliku:

```text
ClinicAdoNet/appsettings.json
```

Przykład:

```json
"DefaultConnection": "Server=localhost,1433;Database=ClinicAdoNet;User Id=sa;Password=admin;TrustServerCertificate=True"
```

## Uruchomienie API

```bash
dotnet restore
dotnet build
dotnet run --project ClinicAdoNet
```

## Endpointy

- GET `/api/appointments`
- GET `/api/appointments/{idAppointment}`
- POST `/api/appointments`
- PUT `/api/appointments/{idAppointment}`
- DELETE `/api/appointments/{idAppointment}`

## Filtrowanie wizyt

Endpoint `GET /api/appointments` obsługuje opcjonalne filtry:

- `status`
- `patientLastName`
- `idDoctor`

Przykład:

```http
GET /api/appointments?status=Scheduled&patientLastName=Erbetowski&idDoctor=1
```

## Statusy HTTP

- `200 OK`
- `201 Created`
- `204 No Content`
- `400 Bad Request`
- `404 Not Found`
- `409 Conflict`