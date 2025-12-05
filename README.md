
# HttpRequestLibrary-SqlServer

A robust, efficient, and flexible HTTP request library for Microsoft SQL Server using .NET CLR integration. This project is designed to work with **all versions of SQL Server that support CLR**—from legacy to modern—providing a powerful and secure alternative to the standard SQL Server HTTP request mechanisms.

---

## **Why HttpRequestLibrary?**

- **Universal Compatibility:**  
  Works seamlessly with both legacy and modern SQL Server instances (2005+), wherever CLR is supported.
- **Superior Efficiency:**  
  Delivers significantly better performance and reliability compared to built-in solutions like `sp_OACreate` or OLE Automation procedures.
- **Robust Error Handling:**  
  Handles errors gracefully, with clear feedback, strict payload/response size limits, and safe header management.
- **Flexible Integration:**  
  Supports all major HTTP verbs, custom headers via JSON, configurable timeouts, and usage as a table-valued SQL function.
- **Maintainable & Extensible:**  
  Written in C#, fully versionable, and easy to update or adapt to new requirements.

---

## **Installation Instructions**

### 1. Compile the Code

Open a terminal in the root of your project folder and run:

```sh
dotnet clean
dotnet restore
dotnet build -c Release -v detailed > build.log
```

### 2. Merge All DLLs into a Single Assembly

Use ILRepack to merge dependencies:

```sh
C:\Users\{your_user}\.nuget\packages\ilrepack\2.0.34\tools\ILRepack.exe /out:C:\Users\{your_user}\source\repos\HttpRequestLibrary\bin\Release\net48\HttpRequestLibrary.dll C:\Users\{your_user}\source\repos\HttpRequestLibrary\bin\Release\net48\HttpRequestLibrary.dll /internalize /verbose
```

*(Replace `{your_user}` with your Windows username and adjust the paths as needed)*

### 3. Generate the Hash for SQL Server Assembly Signing

Run the following PowerShell commands:

```powershell
# Path to the DLL
$dllPath = "C:\Users\{your_user}\source\repos\HttpRequestLibrary\bin\Release\net48\HttpRequestLibrary.dll"

# Calculate SHA512 hash
$hash = Get-FileHash -Path $dllPath -Algorithm SHA512

# Convert the hash to a binary string in the format 0xA1B2C3...
$binaryHex = "0x" + ($hash.Hash -split "(..)" | Where-Object { $_ } | ForEach-Object { $_ }) -join ""

# Output the hash
Write-Host ($binaryHex -replace " ", "")
```

Copy the generated hash for use in the next step.

---

### 4. Register the Assembly in SQL Server

In SQL Server Management Studio (SSMS), connect to the `master` database and execute:

```sql
EXEC sp_add_trusted_assembly 
    @hash = {paste_the_generated_hash_here},
    @description = N'HttpRequestLibrary for HTTP requests';
```

Register the assembly (update the path if needed):

```sql
CREATE ASSEMBLY HttpRequestLibrary
FROM 'C:\Users\{your_user}\source\repos\HttpRequestLibrary\bin\Release\net48\HttpRequestLibrary.dll'
WITH PERMISSION_SET = UNSAFE;
```

---

### 5. Create the Table-Valued Function

```sql
CREATE FUNCTION dbo.HttpRequest
(
    @method NVARCHAR(4000),
    @url NVARCHAR(MAX),
    @headers NVARCHAR(MAX),
    @timeout INT,
    @payload NVARCHAR(MAX),
    @skipCertificateValidation BIT
)
RETURNS TABLE
(
    statusCode INT,
    response NVARCHAR(MAX),
    timing BIGINT
)
AS
EXTERNAL NAME HttpRequestLibrary.[HttpRequestLibrary.HttpRequest].DllHttpRequest;
```

---

## **Usage Example**

```sql
SELECT * FROM dbo.HttpRequest(
    'GET',
    'https://api.example.com/data',
    '[{"Authorization":"Bearer <your_token>"}, {"Content-Type":"application/json"}]',
    3000,
    NULL,
    NULL
);
```

---

## **Security and Best Practices**

- Only trusted users or database roles should have access to this assembly and the function.
- This function requires `UNSAFE` CLR permissions due to HTTP access.
- Always validate external data and minimize exposure to sensitive information in headers and payloads.

---

## **Project Motivation**

Although there are more modern integration options for the latest SQL Server and cloud environments, this project was designed to address **the need for a robust, high-performance, and maintainable HTTP request solution that works everywhere SQL Server supports CLR**—from the oldest supported versions to the newest.

It is not just a legacy solution:  
HttpRequestLibrary is suitable for **all SQL Server environments**, offering a much more reliable and secure alternative to the traditional built-in procedures, and is ready to be used as a drop-in replacement or upgrade for existing solutions.

---

## **License**

[MIT License](LICENSE)

---

## **Contributing**

Contributions are welcome! Please open issues or pull requests for improvements, fixes, or new features.

---

## **Authors**

Developed and maintained by Rodrigo Kmiecik.

---
