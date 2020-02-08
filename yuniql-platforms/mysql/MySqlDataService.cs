﻿using System;
using System.Collections.Generic;
using System.Data;
using Yuniql.Extensibility;
using MySql.Data.MySqlClient;

namespace Yuniql.MySql
{
    public class MySqlDataService : IDataService
    {
        private string _connectionString;
        private readonly ITraceService _traceService;

        public MySqlDataService(ITraceService traceService)
        {
            this._traceService = traceService;
        }

        public void Initialize(string connectionString)
        {
            this._connectionString = connectionString;
        }

        public bool IsAtomicDDLSupported => false;

        public bool IsSchemaSupported { get; } = false;

        public IDbConnection CreateConnection()
        {
            return new MySqlConnection(_connectionString);
        }

        public IDbConnection CreateMasterConnection()
        {
            var masterConnectionStringBuilder = new MySqlConnectionStringBuilder(_connectionString);
            masterConnectionStringBuilder.Database = "INFORMATION_SCHEMA";

            return new MySqlConnection(masterConnectionStringBuilder.ConnectionString);
        }

        public List<string> BreakStatements(string sqlStatementRaw)
        {
            return new List<string> { sqlStatementRaw };
        }

        public ConnectionInfo GetConnectionInfo()
        {
            var connectionStringBuilder = new MySqlConnectionStringBuilder(_connectionString);
            return new ConnectionInfo { DataSource = connectionStringBuilder.Server, Database = connectionStringBuilder.Database };
        }

        public string GetSqlForCheckIfDatabaseExists()
            => @"SELECT 1 FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{0}';";

        public string GetSqlForCreateDatabase()
            => @"CREATE DATABASE `{0}`;";

        public string GetSqlForCheckIfDatabaseConfigured()
            => @"SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{0}' AND TABLE_NAME = '__YuniqlDbVersion' LIMIT 1;";

        public string GetSqlForConfigureDatabase()
            => @"
                CREATE TABLE __YuniqlDbVersion (
	                SequenceId INT AUTO_INCREMENT PRIMARY KEY NOT NULL,
	                Version VARCHAR(512) NOT NULL,
	                AppliedOnUtc TIMESTAMP NOT NULL,
	                AppliedByUser VARCHAR(32) NOT NULL,
	                AppliedByTool VARCHAR(32) NULL,
	                AppliedByToolVersion VARCHAR(16) NULL,
	                AdditionalArtifacts BLOB NULL,
	                CONSTRAINT IX___YuniqlDbVersion UNIQUE (Version)
                ) ENGINE=InnoDB;
            ";

        public string GetSqlForGetCurrentVersion()
            => @"SELECT Version FROM __YuniqlDbVersion ORDER BY SequenceId DESC LIMIT 1;";

        public string GetSqlForGetAllVersions()
            => @"SELECT SequenceId, Version, AppliedOnUtc, AppliedByUser FROM __YuniqlDbVersion ORDER BY Version ASC;";

        public string GetSqlForUpdateVersion()
            => @"INSERT INTO __YuniqlDbVersion (Version, AppliedOnUtc, AppliedByUser) VALUES ('{0}', UTC_TIMESTAMP(), CURRENT_USER());";
    }
}