using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows.Forms;
using OfficeOpenXml;
using Serilog;
using HtmlAgilityPack;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;
using HtmlNode = HtmlAgilityPack.HtmlNode;
using System.Net.Http;

namespace IdentityFlowValidator
{
    internal class Program
    {
        private static HttpClient httpClient = null!;
        private static HttpClientHandler? handler;
        private static ILogger logger = null!;
        private static readonly CookieContainer cookieContainer = new CookieContainer();

        // Login URL used for checks (set to the Google redirect URL requested)
        private const string TARGET_LOGIN_URL = "https://www.google.com/url?sa=t&rct=j&q=&esrc=s&source=web&cd=&cad=rja&uact=8&ved=2ahUKEwj-9oubidyRAxUphf0HHVWQFmMQFnoECBwQAQ&url=https%3A%2F%2Faccount.battle.net%2F&usg=AOvVaw0JF65Akczat7PoTr8uurS2&opi=89978449";

        // Entry: STA thread required for OpenFileDialog
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                MainAsync(args).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal startup error: {ex.Message}");
                return 1;
            }
        }

        static async Task MainAsync(string[] args)
        {
            logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File("battlenet_checker.log",
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                    retainedFileCountLimit: 10)
                .CreateLogger();

            logger.Information("=== Battle.net Phone Checker v2.0 Started ===");
            logger.Information("Process ID: {ProcessId}", Environment.ProcessId);
            logger.Information("Machine: {MachineName}", Environment.MachineName);

            try
            {
                EnsureEpplusLicense();
                ConfigureHttpClient();

                Console.WriteLine("=== Battle.net Phone Checker v2.0 ===");
                Console.WriteLine("Click OK to select your Excel file...");

                string? filePath = SelectExcelFile();

                if (string.IsNullOrWhiteSpace(filePath))
                {
                    logger.Error("No file selected");
                    Console.WriteLine("No file selected. Exiting...");
                    return;
                }

                Console.WriteLine($"Selected file: {filePath}");
                logger.Information("Selected Excel file: {FilePath}", filePath);

                await ProcessExcelFile(filePath);
            }
            catch (Exception ex)
            {
                logger.Fatal(ex, "Fatal error occurred during execution");
                Console.WriteLine($"Fatal Error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
            }
            finally
            {
                try
                {
                    httpClient?.Dispose();
                    Log.CloseAndFlush();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during cleanup: {ex.Message}");
                }
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        private static void EnsureEpplusLicense()
        {
            try
            {
                ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
                logger?.Debug("EPPlus: set ExcelPackage.LicenseContext = NonCommercial");
                return;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "EPPlus: ExcelPackage.LicenseContext assignment failed or not available.");
            }

            try
            {
                var excelPkgType = typeof(ExcelPackage);
                var licenseProp = excelPkgType.GetProperty("License", BindingFlags.Static | BindingFlags.Public);
                if (licenseProp != null && licenseProp.CanWrite)
                {
                    var targetType = licenseProp.PropertyType;

                    if (targetType.IsEnum)
                    {
                        try
                        {
                            var enumValue = Enum.Parse(targetType, "NonCommercial", ignoreCase: true);
                            licenseProp.SetValue(null, enumValue);
                            logger?.Debug("EPPlus: set ExcelPackage.License enum via reflection");
                            return;
                        }
                        catch { }
                    }

                    if (targetType.IsAssignableFrom(typeof(OfficeOpenXml.LicenseContext)))
                    {
                        licenseProp.SetValue(null, OfficeOpenXml.LicenseContext.NonCommercial);
                        logger?.Debug("EPPlus: set ExcelPackage.License (LicenseContext) via reflection");
                        return;
                    }

                    var ctor = targetType.GetConstructor(Type.EmptyTypes);
                    if (ctor != null)
                    {
                        var licenseInstance = ctor.Invoke(null);
                        var ctxProp = targetType.GetProperty("Context", BindingFlags.Public | BindingFlags.Instance)
                                   ?? targetType.GetProperty("LicenseContext", BindingFlags.Public | BindingFlags.Instance);
                        if (ctxProp != null && ctxProp.PropertyType.IsEnum)
                        {
                            try
                            {
                                var ctxValue = Enum.Parse(ctxProp.PropertyType, "NonCommercial", ignoreCase: true);
                                ctxProp.SetValue(licenseInstance, ctxValue);
                                licenseProp.SetValue(null, licenseInstance);
                                logger?.Debug("EPPlus: created license instance and set Context via reflection");
                                return;
                            }
                            catch { }
                        }

                        var setMethod = targetType.GetMethod("SetLicenseContext", BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                                        ?? targetType.GetMethod("SetLicense", BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                        if (setMethod != null)
                        {
                            var paramType = setMethod.GetParameters().FirstOrDefault()?.ParameterType;
                            if (paramType != null)
                            {
                                object? arg = null;
                                if (paramType.IsEnum)
                                {
                                    try { arg = Enum.Parse(paramType, "NonCommercial", ignoreCase: true); } catch { arg = null; }
                                }
                                else if (paramType.IsAssignableFrom(typeof(OfficeOpenXml.LicenseContext)))
                                {
                                    arg = OfficeOpenXml.LicenseContext.NonCommercial;
                                }

                                if (arg != null)
                                {
                                    if (setMethod.IsStatic)
                                        setMethod.Invoke(null, new[] { arg });
                                    else
                                        setMethod.Invoke(licenseInstance, new[] { arg });

                                    if (!setMethod.IsStatic && licenseProp.CanWrite)
                                        licenseProp.SetValue(null, licenseInstance);

                                    logger?.Debug("EPPlus: invoked SetLicense/SetLicenseContext via reflection");
                                    return;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Warning(ex, "EPPlus: reflection-based license set failed");
            }

            logger?.Warning("EPPlus: could not set license automatically. If using EPPlus 8.x follow EPPlus licensing documentation.");
        }

        private static string? SelectExcelFile()
        {
            try
            {
                using var openFileDialog = new OpenFileDialog();

                openFileDialog.Title = "Select Excel File with Phone Numbers";
                openFileDialog.Filter = "Excel Files|*.xlsx;*.xls|All Files|*.*";
                openFileDialog.FilterIndex = 1;
                openFileDialog.RestoreDirectory = true;
                openFileDialog.CheckFileExists = true;
                openFileDialog.CheckPathExists = true;
                openFileDialog.Multiselect = false;

                string initialDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (!Directory.Exists(initialDir))
                {
                    initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }
                openFileDialog.InitialDirectory = initialDir;

                logger.Debug("Opening file selection dialog");

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    var selectedFile = openFileDialog.FileName;
                    logger.Information("File selected: {FilePath}", selectedFile);
                    return selectedFile;
                }
                else
                {
                    logger.Information("File selection cancelled by user");
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error opening file selection dialog");
                Console.WriteLine($"Error opening file dialog: {ex.Message}");

                Console.WriteLine("Falling back to manual file path entry...");
                Console.WriteLine("Enter the path to the Excel file:");
                return Console.ReadLine()?.Trim('"');
            }
        }

        private static void ConfigureHttpClient()
        {
            try
            {
                handler = new HttpClientHandler()
                {
                    CookieContainer = cookieContainer,
                    UseCookies = true
                };

                httpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(Config.HTTP_TIMEOUT_SECONDS)
                };

                SetHttpHeaders();

                logger.Information("HttpClient configured successfully");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to configure HttpClient");
                throw;
            }
        }

        private static void SetHttpHeaders()
        {
            httpClient.DefaultRequestHeaders.Clear();

            var userAgent = Config.USER_AGENTS[Random.Shared.Next(Config.USER_AGENTS.Length)];
            httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
            httpClient.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
            httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
            httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
            httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
            httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");

            logger.Debug("HTTP headers set with User-Agent: {UserAgent}", userAgent);
        }

        private static async Task ProcessExcelFile(string filePath)
        {
            logger.Information("Processing Excel file: {FilePath}", filePath);

            try
            {
                using var package = new ExcelPackage(new FileInfo(filePath));
                var worksheet = package.Workbook.Worksheets.FirstOrDefault();

                if (worksheet == null)
                {
                    logger.Error("No worksheet found in the Excel file");
                    Console.WriteLine("No worksheet found in the Excel file.");
                    return;
                }

                int rowCount = worksheet.Dimension?.Rows ?? 0;
                if (rowCount == 0)
                {
                    logger.Warning("Excel file appears to be empty");
                    Console.WriteLine("Excel file appears to be empty.");
                    return;
                }

                logger.Information("Found {RowCount} rows in the worksheet", rowCount);

                var rawPhoneNumbers = new List<string>();

                for (int row = 1; row <= rowCount; row++)
                {
                    var cellValue = worksheet.Cells[row, 1].Value?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(cellValue))
                    {
                        rawPhoneNumbers.Add(cellValue);
                    }
                }

                logger.Information("Extracted {PhoneCount} phone numbers from Excel", rawPhoneNumbers.Count);

                if (rawPhoneNumbers.Count == 0)
                {
                    logger.Warning("No phone numbers found in the Excel file");
                    Console.WriteLine("No phone numbers found in the Excel file.");
                    return;
                }

                var phoneNumbers = ValidateAndCleanPhoneNumbers(rawPhoneNumbers);

                if (phoneNumbers.Count == 0)
                {
                    logger.Error("No valid phone numbers found after validation");
                    Console.WriteLine("No valid phone numbers found after validation.");
                    return;
                }

                logger.Information("Valid phone numbers after validation: {ValidCount}", phoneNumbers.Count);

                await ProcessPhoneNumbers(phoneNumbers);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error processing Excel file: {FilePath}", filePath);
                throw;
            }
        }

        private static List<string> ValidateAndCleanPhoneNumbers(List<string> phoneNumbers)
        {
            var validNumbers = new List<string>();
            var phoneRegex = new Regex(@"^\+?[\d\s\-\(\)\.]{7,20}$");

            logger.Information("Validating {Count} phone numbers", phoneNumbers.Count);

            foreach (var number in phoneNumbers)
            {
                try
                {
                    var cleanNumber = CleanPhoneNumber(number);
                    if (phoneRegex.IsMatch(cleanNumber) && cleanNumber.Length >= 7)
                    {
                        validNumbers.Add(cleanNumber);
                        logger.Debug("Valid phone number: {PhoneNumber}", cleanNumber);
                    }
                    else
                    {
                        logger.Warning("Invalid phone number format skipped: {PhoneNumber}", number);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Error processing phone number: {PhoneNumber}", number);
                }
            }

            logger.Information("Validation complete. Valid numbers: {ValidCount}, Invalid: {InvalidCount}",
                validNumbers.Count, phoneNumbers.Count - validNumbers.Count);

            return validNumbers;
        }

        private static string CleanPhoneNumber(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
                return string.Empty;

            return phoneNumber.Trim()
                .Replace(" ", "")
                .Replace("-", "")
                .Replace("(", "")
                .Replace(")", "")
                .Replace(".", "");
        }

        private static async Task ProcessPhoneNumbers(List<string> phoneNumbers)
        {
            var validNumbers = new List<string>();
            var invalidNumbers = new List<string>();
            var errorNumbers = new List<string>();

            var startTime = DateTime.Now;
            var processed = 0;

            Console.WriteLine($"\n=== Starting validation of {phoneNumbers.Count} phone numbers ===");
            Console.WriteLine("This process may take a while. Please be patient...\n");

            for (int i = 0; i < phoneNumbers.Count; i++)
            {
                var phoneNumber = phoneNumbers[i];
                processed++;

                var elapsed = DateTime.Now - startTime;
                var avgTimePerNumber = elapsed.TotalSeconds / processed;
                var estimatedTimeRemaining = TimeSpan.FromSeconds(avgTimePerNumber * (phoneNumbers.Count - processed));

                Console.WriteLine($"\nChecking {i + 1}/{phoneNumbers.Count}: {phoneNumber}");
                Console.WriteLine($"Progress: {(double)processed / phoneNumbers.Count:P1} | ETA: {estimatedTimeRemaining:hh\\:mm\\:ss}");

                try
                {
                    bool isValid = await TryLoginCheck(phoneNumber);

                    if (isValid)
                    {
                        validNumbers.Add(phoneNumber);
                        logger.Information("VALID: {PhoneNumber} - Account exists", phoneNumber);
                        Console.WriteLine($"✓ {phoneNumber} - VALID (Account exists)");
                    }
                    else
                    {
                        invalidNumbers.Add(phoneNumber);
                        logger.Information("INVALID: {PhoneNumber} - No account found", phoneNumber);
                        Console.WriteLine($"✗ {phoneNumber} - INVALID (No account found)");
                    }
                }
                catch (Exception ex)
                {
                    errorNumbers.Add(phoneNumber);
                    logger.Error(ex, "Error checking phone number: {PhoneNumber}", phoneNumber);
                    Console.WriteLine($"! {phoneNumber} - ERROR: {ex.Message}");
                }

                if (i < phoneNumbers.Count - 1)
                {
                    var delayMs = CalculateDelay(validNumbers.Count, invalidNumbers.Count, errorNumbers.Count);
                    logger.Debug("Waiting {DelayMs}ms before next request", delayMs);
                    await Task.Delay(delayMs);
                }
            }

            var totalTime = DateTime.Now - startTime;
            logger.Information("Processing completed in {TotalTime}", totalTime);

            await DisplayAndSaveResults(validNumbers, invalidNumbers, errorNumbers, totalTime, phoneNumbers.Count);
        }

        private static int CalculateDelay(int validCount, int invalidCount, int errorCount)
        {
            var totalProcessed = validCount + invalidCount + errorCount;
            if (totalProcessed == 0)
                return Random.Shared.Next(Config.MIN_DELAY_MS, Config.MAX_DELAY_MS);

            var errorRate = (double)errorCount / totalProcessed;

            if (errorRate > 0.3) // >30% errors
            {
                logger.Warning("High error rate detected ({ErrorRate:P1}). Increasing delay.", errorRate);
                return Random.Shared.Next(8000, 12000); // 8-12s
            }
            else if (errorRate > 0.1) // >10% errors
            {
                logger.Debug("Moderate error rate detected ({ErrorRate:P1}). Slightly increasing delay.", errorRate);
                return Random.Shared.Next(5000, 8000); // 5-8s
            }
            else
            {
                return Random.Shared.Next(Config.MIN_DELAY_MS, Config.MAX_DELAY_MS); // normal 3-6s
            }
        }

        // New: Attempt to login (first-step) with the identifier and detect whether the site asks for a password
        // Enhanced logging: logs success/failure, reason, status and a short response snippet for diagnosis.
        private static async Task<bool> TryLoginCheck(string identifier)
        {
            for (int attempt = 1; attempt <= Config.MAX_RETRIES; attempt++)
            {
                string reason = "unknown";
                string responseContent = string.Empty;
                HttpStatusCode? statusCode = null;
                Uri? finalUri = null;

                try
                {
                    logger.Debug("Login-check attempt {Attempt}/{MaxAttempts} for {Identifier}", attempt, Config.MAX_RETRIES, identifier);

                    if (attempt > 1)
                    {
                        RotateUserAgent();
                        await Task.Delay(1000);
                    }

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Config.HTTP_TIMEOUT_SECONDS));

                    // GET login page
                    logger.Debug("Getting login page for {Identifier}", identifier);
                    var getResponse = await httpClient.GetAsync(TARGET_LOGIN_URL, cts.Token);
                    getResponse.EnsureSuccessStatusCode();
                    var pageContent = await getResponse.Content.ReadAsStringAsync(cts.Token);

                    var doc = new HtmlDocument();
                    doc.LoadHtml(pageContent);

                    var form = doc.DocumentNode.SelectSingleNode("//form") ?? doc.DocumentNode;
                    var formAction = form.GetAttributeValue("action", TARGET_LOGIN_URL);

                    string[] candidateNames = { "accountIdentifier", "username", "user", "login", "email", "accountName", "identifier" };
                    var idInput = form.SelectSingleNode(".//input[@type='text' or @type='email' or not(@type)]") ??
                                  form.SelectSingleNode(".//input[contains(@name,'account') or contains(@name,'user') or contains(@name,'email') or contains(@name,'login')]");
                    string idFieldName = idInput?.GetAttributeValue("name", "") ?? "";

                    if (string.IsNullOrEmpty(idFieldName))
                    {
                        foreach (var cand in candidateNames)
                        {
                            var node = form.SelectSingleNode($".//input[@name='{cand}']");
                            if (node != null) { idFieldName = cand; break; }
                        }
                    }

                    var formData = new List<KeyValuePair<string, string>>();
                    var hiddenInputs = form.SelectNodes(".//input[@type='hidden']");
                    if (hiddenInputs != null)
                    {
                        foreach (var h in hiddenInputs)
                        {
                            var n = h.GetAttributeValue("name", "");
                            var v = h.GetAttributeValue("value", "");
                            if (!string.IsNullOrEmpty(n))
                                formData.Add(new KeyValuePair<string, string>(n, v));
                        }
                    }

                    if (!string.IsNullOrEmpty(idFieldName))
                        formData.Add(new KeyValuePair<string, string>(idFieldName, identifier));
                    else
                        foreach (var cand in candidateNames)
                            formData.Add(new KeyValuePair<string, string>(cand, identifier));

                    var encodedContent = new FormUrlEncodedContent(formData);

                    string submitUrl = formAction;
                    if (!Uri.IsWellFormedUriString(submitUrl, UriKind.Absolute))
                    {
                        var baseUri = new Uri(TARGET_LOGIN_URL);
                        submitUrl = new Uri(baseUri, submitUrl).ToString();
                    }

                    logger.Debug("Submitting login-check for {Identifier} to {Url}", identifier, submitUrl);

                    var postResponse = await httpClient.PostAsync(submitUrl, encodedContent, cts.Token);
                    statusCode = postResponse.StatusCode;

                    // Get effective final URI (HttpClient follows redirects by default)
                    finalUri = postResponse.RequestMessage?.RequestUri ?? new Uri(submitUrl);
                    if (postResponse.Headers.Location != null)
                    {
                        try { finalUri = new Uri(finalUri, postResponse.Headers.Location); } catch { }
                    }

                    logger.Debug("Post finished. Final URI: {FinalUri} | Status: {StatusCode}", finalUri, postResponse.StatusCode);

                    responseContent = await postResponse.Content.ReadAsStringAsync(cts.Token);

                    // Quick URI hint
                    var finalLower = finalUri.ToString().ToLowerInvariant();
                    if (finalLower.Contains("password") || finalLower.Contains("verify") || finalLower.Contains("check") || finalLower.Contains("confirm"))
                    {
                        reason = "Final URI indicates password/verification step";
                        logger.Information("SUCCESS: {Identifier} | Reason: {Reason} | FinalUri: {FinalUri} | Status: {StatusCode}\nResponseSnippet: {Snippet}",
                            identifier, reason, finalUri, statusCode, Truncate(responseContent, 1000));
                        return true;
                    }

                    // HTML/script analysis
                    if (AnalyzeLoginResponse(responseContent))
                    {
                        reason = "Password prompt detected via HTML/script analysis";
                        logger.Information("SUCCESS: {Identifier} | Reason: {Reason} | FinalUri: {FinalUri} | Status: {StatusCode}\nResponseSnippet: {Snippet}",
                            identifier, reason, finalUri, statusCode, Truncate(responseContent, 1000));
                        return true;
                    }

                    // Follow-up GET to final URI in case client-side JS would render the password step from a landing page
                    try
                    {
                        logger.Debug("Performing follow-up GET to final URI for {Identifier}: {FinalUri}", identifier, finalUri);
                        var followResponse = await httpClient.GetAsync(finalUri, cts.Token);
                        var followStatus = followResponse.StatusCode;
                        var followContent = await followResponse.Content.ReadAsStringAsync(cts.Token);

                        if (AnalyzeLoginResponse(followContent))
                        {
                            reason = "Password prompt detected on follow-up GET";
                            logger.Information("SUCCESS: {Identifier} | Reason: {Reason} | FinalUri: {FinalUri} | Status: {StatusCode}\nResponseSnippet: {Snippet}",
                                identifier, reason, finalUri, followStatus, Truncate(followContent, 1000));
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Debug(ex, "Follow-up GET failed for {Identifier}", identifier);
                        // fall through to not-found checks and ambiguous handling
                    }

                    // Explicit negative indicators
                    string[] notFoundIndicators =
                    {
                        "account not found", "no account found", "account does not exist", "couldn't find", "unable to locate",
                        "not a valid account", "no user found", "we couldn't find an account", "invalid email or phone number"
                    };

                    var lower = responseContent.ToLowerInvariant();
                    foreach (var ind in notFoundIndicators)
                    {
                        if (lower.Contains(ind))
                        {
                            reason = $"Account not found indicator detected: '{ind}'";
                            logger.Information("FAIL: {Identifier} | Reason: {Reason} | FinalUri: {FinalUri} | Status: {StatusCode}\nResponseSnippet: {Snippet}",
                                identifier, reason, finalUri, statusCode, Truncate(responseContent, 1000));
                            return false;
                        }
                    }

                    // Ambiguous — save and mark invalid (optionally change to 'unknown')
                    SaveUnclearResponse(identifier, responseContent);
                    reason = "Ambiguous response (no clear password prompt or explicit not-found indicator)";
                    logger.Warning("FAIL (ambiguous): {Identifier} | Reason: {Reason} | FinalUri: {FinalUri} | Status: {StatusCode} | Saved response for manual review",
                        identifier, reason, finalUri, statusCode);
                    return false;
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || ex.CancellationToken.IsCancellationRequested)
                {
                    reason = "Timeout";
                    logger.Warning(ex, "FAIL: {Identifier} | Reason: {Reason} on attempt {Attempt}/{MaxAttempts}", identifier, reason, attempt, Config.MAX_RETRIES);
                    if (attempt == Config.MAX_RETRIES) throw new Exception($"Request timed out after {Config.MAX_RETRIES} attempts");
                }
                catch (HttpRequestException ex) when (IsRateLimited(ex))
                {
                    reason = "Rate limited (HTTP request)";
                    var backoffDelay = Config.RATE_LIMIT_DELAY_MS * attempt;
                    logger.Warning(ex, "Rate limited on attempt {Attempt}/{MaxAttempts} for {Identifier}. Reason: {Reason}. Backing off for {Delay}ms",
                        attempt, Config.MAX_RETRIES, identifier, reason, backoffDelay);
                    await Task.Delay(backoffDelay);
                    if (attempt == Config.MAX_RETRIES) throw new Exception($"Rate limited after {Config.MAX_RETRIES} attempts");
                }
                catch (Exception ex) when (attempt < Config.MAX_RETRIES)
                {
                    reason = $"Transient error: {ex.Message}";
                    logger.Warning(ex, "Transient error on attempt {Attempt}/{MaxAttempts} for {Identifier}. Reason: {Reason}. Retrying...", attempt, Config.MAX_RETRIES, identifier, reason);
                    await Task.Delay(1000 * attempt);
                }
                catch (Exception ex)
                {
                    // Final failure logging
                    reason = $"Unhandled exception: {ex.Message}";
                    logger.Error(ex, "FAIL: {Identifier} | Reason: {Reason}", identifier, reason);
                    throw;
                }
            }

            throw new Exception($"Failed after {Config.MAX_RETRIES} attempts");
        }

        private static bool AnalyzeLoginResponse(string responseContent)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(responseContent);

                // 1) direct password input
                if (doc.DocumentNode.SelectSingleNode("//input[@type='password']") != null)
                    return true;

                // 2) look for forms/containers with 'password' in id/class/name
                var pwdCandidates = doc.DocumentNode.SelectNodes("//*[contains(translate(@id,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'password') or contains(translate(@class,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'password') or contains(translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'password')]");
                if (pwdCandidates != null && pwdCandidates.Any())
                {
                    foreach (var node in pwdCandidates)
                    {
                        if (node.SelectSingleNode(".//input[@type='password']") != null) return true;
                    }
                    // presence of such nodes increases chance — continue to other heuristics
                }

                // 3) Search script tags / inline JSON for password-step flags (common in SPA flows)
                var scripts = doc.DocumentNode.SelectNodes("//script");
                if (scripts != null)
                {
                    foreach (var s in scripts)
                    {
                        var txt = s.InnerText ?? "";
                        var lower = txt.ToLowerInvariant();

                        if (lower.Contains("requirespassword") || lower.Contains("passwordstep") || lower.Contains("show-password") ||
                            lower.Contains("\"password\"") || lower.Contains("ispassword") || lower.Contains("challenge") || lower.Contains("authstep"))
                        {
                            return true;
                        }
                    }
                }

                // 4) strong phrase checks in the visible HTML (not generic "password")
                var bodyLower = responseContent.ToLowerInvariant();
                string[] strongPhrases =
                {
                    "please enter your password",
                    "enter your password",
                    "enter password",
                    "verify your password",
                    "password required",
                    "please provide your password",
                    "we've sent a code to", // may indicate verification step (account exists)
                    "check your phone" // indicates account exists and next step
                };

                foreach (var phrase in strongPhrases)
                    if (bodyLower.Contains(phrase)) return true;

                // 5) explicit not-found indicators
                string[] notFoundIndicators =
                {
                    "account not found", "no account found", "account does not exist", "couldn't find",
                    "unable to locate", "not a valid account", "no user found", "we couldn't find an account",
                    "invalid email or phone number"
                };

                foreach (var ind in notFoundIndicators)
                    if (bodyLower.Contains(ind)) return false;

                // 6) Default: treat as unclear -> false (invalid) to be safe
                return false;
            }
            catch (Exception ex)
            {
                logger?.Warning(ex, "Error analyzing login response");
                return false;
            }
        }

        private static bool IsRateLimited(HttpRequestException ex)
        {
            var message = ex.Message.ToLowerInvariant();
            return message.Contains("429") ||
                   message.Contains("too many requests") ||
                   message.Contains("rate limit") ||
                   message.Contains("throttled");
        }

        private static void RotateUserAgent()
        {
            try
            {
                var randomUserAgent = Config.USER_AGENTS[Random.Shared.Next(Config.USER_AGENTS.Length)];
                httpClient.DefaultRequestHeaders.Remove("User-Agent");
                httpClient.DefaultRequestHeaders.Add("User-Agent", randomUserAgent);
                logger.Debug("Rotated User-Agent to: {UserAgent}", randomUserAgent);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Failed to rotate User-Agent");
            }
        }

        private static string? ExtractCsrfToken(HtmlDocument doc)
        {
            try
            {
                var tokenInput = doc.DocumentNode
                    .SelectSingleNode("//input[@name='_token' or @name='csrf_token' or @name='authenticity_token']");

                if (tokenInput != null)
                {
                    return tokenInput.GetAttributeValue("value", "");
                }

                var metaToken = doc.DocumentNode
                    .SelectSingleNode("//meta[@name='csrf-token' or @name='_token']");

                if (metaToken != null)
                {
                    return metaToken.GetAttributeValue("content", "");
                }

                logger.Debug("No CSRF token found in response");
                return null;
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Error extracting CSRF token");
                return null;
            }
        }

        private static string? ExtractFormAction(HtmlDocument doc)
        {
            try
            {
                var form = doc.DocumentNode.SelectSingleNode("//form");
                return form?.GetAttributeValue("action", "");
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Error extracting form action");
                return null;
            }
        }

        private static void AddHiddenFormFields(HtmlDocument doc, List<KeyValuePair<string, string>> formData)
        {
            try
            {
                var hiddenInputs = doc.DocumentNode
                    .SelectNodes("//input[@type='hidden']");

                if (hiddenInputs != null)
                {
                    foreach (var input in hiddenInputs)
                    {
                        var name = input.GetAttributeValue("name", "");
                        var value = input.GetAttributeValue("value", "");

                        if (!string.IsNullOrEmpty(name) && !formData.Any(kvp => kvp.Key == name))
                        {
                            formData.Add(new(name, value));
                            logger.Debug("Added hidden field: {Name} = {Value}", name, value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Error adding hidden form fields");
            }
        }

        private static void SaveUnclearResponse(string phoneNumber, string responseContent)
        {
            try
            {
                var fileName = $"unclear_response_{CleanPhoneNumber(phoneNumber)}_{DateTime.Now:yyyyMMdd_HHmmss}.html";
                fileName = Regex.Replace(fileName, @"[<>:""/\\|?*]", "_");

                File.WriteAllText(fileName, responseContent);
                logger.Information("Saved unclear response to {FileName} for manual review", fileName);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to save unclear response for {PhoneNumber}", phoneNumber);
            }
        }

        // Helper: truncate long responses for logging
        private static string Truncate(string? s, int maxLength)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.Length <= maxLength) return s;
            return s.Substring(0, maxLength) + "...[truncated]";
        }

        private static async Task DisplayAndSaveResults(List<string> validNumbers, List<string> invalidNumbers,
            List<string> errorNumbers, TimeSpan totalTime, int totalProcessed)
        {
            Console.WriteLine($"\n=== FINAL SUMMARY ===");
            Console.WriteLine($"Total processed: {totalProcessed}");
            Console.WriteLine($"Valid numbers (accounts exist): {validNumbers.Count}");
            Console.WriteLine($"Invalid numbers (no accounts): {invalidNumbers.Count}");
            Console.WriteLine($"Errors encountered: {errorNumbers.Count}");
            Console.WriteLine($"Success rate: {(double)validNumbers.Count / totalProcessed:P1}");
            Console.WriteLine($"Total processing time: {totalTime:hh\\:mm\\:ss}");

            logger.Information("Final Summary - Valid: {ValidCount}, Invalid: {InvalidCount}, Errors: {ErrorCount}, Total Time: {TotalTime}",
                validNumbers.Count, invalidNumbers.Count, errorNumbers.Count, totalTime);

            await SaveResults(validNumbers, invalidNumbers, errorNumbers);
        }

        private static async Task SaveResults(List<string> validNumbers, List<string> invalidNumbers, List<string> errorNumbers)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                if (validNumbers.Any())
                {
                    var validFile = $"valid_numbers_{timestamp}.txt";
                    await File.WriteAllLinesAsync(validFile, validNumbers);
                    logger.Information("Valid numbers saved to: {FileName}", validFile);
                    Console.WriteLine($"✓ Valid numbers saved to: {validFile}");
                }

                if (invalidNumbers.Any())
                {
                    var invalidFile = $"invalid_numbers_{timestamp}.txt";
                    await File.WriteAllLinesAsync(invalidFile, invalidNumbers);
                    logger.Information("Invalid numbers saved to: {FileName}", invalidFile);
                    Console.WriteLine($"✗ Invalid numbers saved to: {invalidFile}");
                }

                if (errorNumbers.Any())
                {
                    var errorFile = $"error_numbers_{timestamp}.txt";
                    await File.WriteAllLinesAsync(errorFile, errorNumbers);
                    logger.Information("Error numbers saved to: {FileName}", errorFile);
                    Console.WriteLine($"! Error numbers saved to: {errorFile}");
                }

                var summaryFile = $"summary_{timestamp}.txt";
                var summary = new StringBuilder();
                summary.AppendLine($"Battle.net Phone Number Validation Summary");
                summary.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                summary.AppendLine($"Process ID: {Environment.ProcessId}");
                summary.AppendLine($"Machine: {Environment.MachineName}");
                summary.AppendLine();
                summary.AppendLine($"=== STATISTICS ===");
                summary.AppendLine($"Total Checked: {validNumbers.Count + invalidNumbers.Count + errorNumbers.Count}");
                summary.AppendLine($"Valid (Account exists): {validNumbers.Count}");
                summary.AppendLine($"Invalid (No account): {invalidNumbers.Count}");
                summary.AppendLine($"Errors: {errorNumbers.Count}");
                summary.AppendLine($"Success Rate: {(double)validNumbers.Count / (validNumbers.Count + invalidNumbers.Count + errorNumbers.Count):P2}");
                summary.AppendLine();

                if (validNumbers.Any())
                {
                    summary.AppendLine("=== VALID NUMBERS (Accounts exist) ===");
                    validNumbers.ForEach(num => summary.AppendLine($"  {num}"));
                    summary.AppendLine();
                }

                if (invalidNumbers.Any())
                {
                    summary.AppendLine("=== INVALID NUMBERS (No accounts) ===");
                    invalidNumbers.ForEach(num => summary.AppendLine($"  {num}"));
                    summary.AppendLine();
                }

                if (errorNumbers.Any())
                {
                    summary.AppendLine("=== ERROR NUMBERS (Processing failed) ===");
                    errorNumbers.ForEach(num => summary.AppendLine($"  {num}"));
                }

                await File.WriteAllTextAsync(summaryFile, summary.ToString());
                logger.Information("Summary saved to: {FileName}", summaryFile);
                Console.WriteLine($"📄 Summary saved to: {summaryFile}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to save results to files");
                Console.WriteLine($"Error saving results: {ex.Message}");
            }
        }
    }
}