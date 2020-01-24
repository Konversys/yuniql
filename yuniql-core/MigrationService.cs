﻿using Yuniql.Extensibility;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.IO;

namespace Yuniql.Core
{
    public class MigrationService : IMigrationService
    {
        private readonly ILocalVersionService _localVersionService;
        private readonly IDataService _dataService;
        private readonly IBulkImportService _bulkImportService;
        private readonly ITokenReplacementService _tokenReplacementService;
        private readonly IDirectoryService _directoryService;
        private readonly IFileService _fileService;
        private readonly ITraceService _traceService;
        private readonly IConfigurationDataService _configurationDataService;

        public MigrationService(
            ILocalVersionService localVersionService,
            IDataService dataService,
            IBulkImportService bulkImportService,
            ITokenReplacementService tokenReplacementService,
            IDirectoryService directoryService,
            IFileService fileService,
            ITraceService traceService,
            IConfigurationDataService configurationDataService)
        {
            this._localVersionService = localVersionService;
            this._dataService = dataService;
            this._bulkImportService = bulkImportService;
            this._tokenReplacementService = tokenReplacementService;
            this._directoryService = directoryService;
            this._fileService = fileService;
            this._traceService = traceService;
            this._configurationDataService = configurationDataService;
        }

        public void Initialize(
            string connectionString,
            int? commandTimeout = null)
        {
            //initialize dependencies
            _dataService.Initialize(connectionString);
            _bulkImportService.Initialize(connectionString);
        }

        public void Run(
            string workingPath,
            string targetVersion = null,
            bool? autoCreateDatabase = false,
            List<KeyValuePair<string, string>> tokenKeyPairs = null,
            bool? verifyOnly = false,
            string delimiter = null,
            int? commandTimeout = null,
            int? batchSize = null
         )
        {
            //validate workspace structure
            _localVersionService.Validate(workingPath);

            //when uncomitted run is not supported, fail migration and throw exceptions
            if (verifyOnly.HasValue && verifyOnly == true && !_dataService.IsAtomicDDLSupported)
            {
                throw new NotSupportedException("Yuniql.Verify is not supported in the target platform. " +
                    "The feature requires support for atomic DDL operations. " +
                    "An atomic DDL operations ensures creation of tables, views and other objects and data are rolledback in case of error. " +
                    "For more information see WIKI.");
            }

            //when no target version specified, we use the latest local version 
            if (string.IsNullOrEmpty(targetVersion))
            {
                targetVersion = _localVersionService.GetLatestVersion(workingPath);
                _traceService.Info($"No explicit target version requested. We'll use latest available locally {targetVersion} on {workingPath}.");
            }

            var connectionInfo = _dataService.GetConnectionInfo();
            var targetDatabaseName = connectionInfo.Database;
            var targetDatabaseServer = connectionInfo.DataSource;

            //create the database, we need this to be outside of the transaction scope
            //in an event of failure, users have to manually drop the auto-created database
            var targetDatabaseExists = _configurationDataService.IsDatabaseExists();
            if (!targetDatabaseExists && (autoCreateDatabase.HasValue && autoCreateDatabase == true))
            {
                _traceService.Info($"Target database does not exist. Creating database {targetDatabaseName} on {targetDatabaseServer}.");
                _configurationDataService.CreateDatabase();
                _traceService.Info($"Created database {targetDatabaseName} on {targetDatabaseServer}.");
            }

            //check if database has been pre-configured to support migration and setup when its not
            var targetDatabaseConfigured = _configurationDataService.IsDatabaseConfigured();
            if (!targetDatabaseConfigured)
            {
                _traceService.Info($"Target database {targetDatabaseName} on {targetDatabaseServer} not yet configured for migration.");
                _configurationDataService.ConfigureDatabase();
                _traceService.Info($"Configured database migration support for {targetDatabaseName} on {targetDatabaseServer}.");
            }

            var dbVersions = _configurationDataService.GetAllVersions()
                .Select(dv => dv.Version)
                .OrderBy(v => v)
                .ToList();

            var targeDatabaseLatest = IsTargetDatabaseLatest(targetVersion);
            if (!targeDatabaseLatest)
            {
                //enclose all executions in a single transaction
                using (var connection = _dataService.CreateConnection())
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            //check if database has been pre-configured and execute init scripts
                            if (!targetDatabaseConfigured)
                            {
                                //runs all scripts in the _init folder
                                RunNonVersionScripts(connection, transaction, Path.Combine(workingPath, "_init"), tokenKeyPairs, delimiter: delimiter, commandTimeout: commandTimeout);
                                _traceService.Info($"Executed script files on {Path.Combine(workingPath, "_init")}");
                            }

                            //checks if target database already runs the latest version and skips work if it already is
                            //runs all scripts in the _pre folder and subfolders
                            RunNonVersionScripts(connection, transaction, Path.Combine(workingPath, "_pre"), tokenKeyPairs, delimiter: delimiter, commandTimeout: commandTimeout);
                            _traceService.Info($"Executed script files on {Path.Combine(workingPath, "_pre")}");

                            //runs all scripts int the vxx.xx folders and subfolders
                            RunVersionScripts(connection, transaction, dbVersions, workingPath, targetVersion, tokenKeyPairs, delimiter: delimiter, commandTimeout: commandTimeout, batchSize: batchSize);

                            //runs all scripts in the _draft folder and subfolders
                            RunNonVersionScripts(connection, transaction, Path.Combine(workingPath, "_draft"), tokenKeyPairs, delimiter: delimiter, commandTimeout: commandTimeout);
                            _traceService.Info($"Executed script files on {Path.Combine(workingPath, "_draft")}");

                            //runs all scripts in the _post folder and subfolders
                            RunNonVersionScripts(connection, transaction, Path.Combine(workingPath, "_post"), tokenKeyPairs, delimiter: delimiter, commandTimeout: commandTimeout);
                            _traceService.Info($"Executed script files on {Path.Combine(workingPath, "_post")}");

                            //when true, the execution is an uncommitted transaction 
                            //and only for purpose of testing if all can go well when it run to the target environment
                            if (verifyOnly.HasValue && verifyOnly == true)
                                transaction.Rollback();
                            else
                                transaction.Commit();
                        }
                        catch (Exception)
                        {
                            transaction.Rollback();
                            throw;
                        }

                    }
                }
            }
            else
            {
                _traceService.Info($"Target database runs the latest version already. No changes made at {targetDatabaseName} on {targetDatabaseServer}.");
            }
        }

        private bool IsTargetDatabaseLatest(string targetVersion)
        {
            var dbcv = _configurationDataService.GetCurrentVersion();
            if (string.IsNullOrEmpty(dbcv)) return false;

            var cv = new LocalVersion(dbcv);
            var tv = new LocalVersion(targetVersion);
            return string.Compare(cv.SemVersion, tv.SemVersion) == 1 || //db has more updated than local version
                string.Compare(cv.SemVersion, tv.SemVersion) == 0;      //db has the same version as local version
        }

        public string GetCurrentVersion()
        {
            return _configurationDataService.GetCurrentVersion();
        }

        public List<DbVersion> GetAllVersions()
        {
            return _configurationDataService.GetAllVersions();
        }

        private void RunNonVersionScripts(
            IDbConnection connection,
            IDbTransaction transaction,
            string workingPath,
            List<KeyValuePair<string, string>> tokens = null,
            string delimiter = null,
            int? commandTimeout = null
        )
        {
            var sqlScriptFiles = _directoryService.GetFiles(workingPath, "*.sql").ToList();
            _traceService.Info($"Found the {sqlScriptFiles.Count} script files on {workingPath}");
            _traceService.Info($"{string.Join(@"\r\n\t", sqlScriptFiles.Select(s => new FileInfo(s).Name))}");

            //execute all script files in the target folder
            sqlScriptFiles.Sort();
            sqlScriptFiles.ForEach(scriptFile =>
            {
                //https://stackoverflow.com/questions/25563876/executing-sql-batch-containing-go-statements-in-c-sharp/25564722#25564722
                var sqlStatementRaw = _fileService.ReadAllText(scriptFile);
                var sqlStatements = _dataService.BreakStatements(sqlStatementRaw)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                sqlStatements.ForEach(sqlStatement =>
                {
                    //replace tokens with values from the cli
                    sqlStatement = _tokenReplacementService.Replace(tokens, sqlStatement);

                    _traceService.Debug($"Executing sql statement as part of : {scriptFile}{Environment.NewLine}{sqlStatement}");

                    _configurationDataService.ExecuteSql(
                        connection: connection,
                        commandText: sqlStatement,
                        transaction: transaction,
                        commandTimeout: commandTimeout,
                        traceService: _traceService);
                });

                _traceService.Info($"Executed script file {scriptFile}.");
            });
        }

        private void RunVersionScripts(
            IDbConnection connection,
            IDbTransaction transaction,
            List<string> dbVersions,
            string workingPath,
            string targetVersion,
            List<KeyValuePair<string, string>> tokens = null,
            string delimiter = null,
            int? commandTimeout = null,
            int? batchSize = null
        )
        {
            //excludes all versions already executed
            var versionDirectories = _directoryService.GetDirectories(workingPath, "v*.*")
                .Where(v => !dbVersions.Contains(new DirectoryInfo(v).Name))
                .ToList();

            //exclude all versions greater than the target version
            if (!string.IsNullOrEmpty(targetVersion))
            {
                versionDirectories.RemoveAll(v =>
                {
                    var cv = new LocalVersion(new DirectoryInfo(v).Name);
                    var tv = new LocalVersion(targetVersion);

                    return string.Compare(cv.SemVersion, tv.SemVersion) == 1;
                });
            }

            //execute all sql scripts in the version folders
            if (versionDirectories.Any())
            {
                versionDirectories.Sort();
                versionDirectories.ForEach(versionDirectory =>
                {
                    try
                    {
                        //run scripts in all sub-directories
                        var versionSubDirectories = _directoryService.GetDirectories(versionDirectory, "*", SearchOption.AllDirectories).ToList();
                        versionSubDirectories.Sort();
                        versionSubDirectories.ForEach(versionSubDirectory =>
                        {
                            RunSqlScripts(connection, transaction, versionSubDirectory, tokens, commandTimeout);
                        });

                        //run all scripts in the current version folder
                        RunSqlScripts(connection, transaction, versionDirectory, tokens, commandTimeout);

                        //import csv files into tables of the the same filename as the csv
                        RunBulkImport(connection, transaction, versionDirectory, delimiter, batchSize, commandTimeout);

                        //update db version
                        var versionName = new DirectoryInfo(versionDirectory).Name;
                        _configurationDataService.UpdateVersion(connection, transaction, versionName, commandTimeout: commandTimeout);

                        _traceService.Info($"Completed migration to version {versionDirectory}");
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                });
            }
            else
            {
                var connectionInfo = _dataService.GetConnectionInfo();
                _traceService.Info($"Target database is updated. No migration step executed at {connectionInfo.Database} on {connectionInfo.DataSource}.");
            }
        }

        private void RunBulkImport(
            IDbConnection connection,
            IDbTransaction transaction,
            string versionFullPath,
            string delimiter = null,
            int? batchSize = null,
            int? commandTimeout = null
        )
        {
            //execute all script files in the version folder
            var bulkFiles = _directoryService.GetFiles(versionFullPath, "*.csv").ToList();
            bulkFiles.Sort();

            _traceService.Info($"Found the {bulkFiles.Count} bulk files on {versionFullPath}");
            _traceService.Info($"{string.Join(@"\r\n\t", bulkFiles.Select(s => new FileInfo(s).Name))}");

            bulkFiles.ForEach(csvFile =>
            {
                _bulkImportService.Run(connection, transaction, csvFile, delimiter, batchSize: batchSize, commandTimeout: commandTimeout);
                _traceService.Info($"Imported bulk file {csvFile}.");
            });
        }

        private void RunSqlScripts(
            IDbConnection connection,
            IDbTransaction transaction,
            string versionFullPath,
            List<KeyValuePair<string, string>> tokens = null,
            int? commandTimeout = null
        )
        {
            var sqlScriptFiles = _directoryService.GetFiles(versionFullPath, "*.sql").ToList();
            _traceService.Info($"Found the {sqlScriptFiles.Count} script files on {versionFullPath}");
            _traceService.Info($"{string.Join(@"\r\n\t", sqlScriptFiles.Select(s => new FileInfo(s).Name))}");

            //execute all script files in the version folder
            sqlScriptFiles.Sort();
            sqlScriptFiles
                .ForEach(scriptFile =>
            {
                var sqlStatementRaw = _fileService.ReadAllText(scriptFile);
                var sqlStatements = _dataService.BreakStatements(sqlStatementRaw)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                ;
                sqlStatements.ForEach(sqlStatement =>
                {
                    sqlStatement = _tokenReplacementService.Replace(tokens, sqlStatement);

                    _traceService.Debug($"Executing sql statement as part of : {scriptFile}{Environment.NewLine}{sqlStatement}");
                    _configurationDataService.ExecuteSql(
                        connection: connection,
                        commandText: sqlStatement,
                        transaction: transaction,
                        commandTimeout: commandTimeout,
                        traceService: _traceService);
                });

                _traceService.Info($"Executed script file {scriptFile}.");
            });
        }

        public void Erase(
            string workingPath,
            List<KeyValuePair<string, string>> tokenKeyPairs = null,
            int? commandTimeout = null
        )
        {
            //enclose all executions in a single transaction
            using (var connection = _dataService.CreateConnection())
            {
                connection.KeepOpen();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        //runs all scripts in the _erase folder
                        RunNonVersionScripts(connection, transaction, Path.Combine(workingPath, "_erase"), tokenKeyPairs, DefaultConstants.Delimiter, commandTimeout);
                        _traceService.Info($"Executed script files on {Path.Combine(workingPath, "_erase")}");

                        transaction.Commit();
                    }
                    catch (Exception)
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }
    }
}
