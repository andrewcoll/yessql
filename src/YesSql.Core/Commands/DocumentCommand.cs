﻿using System.Data;
using System.Reflection;
using System.Threading.Tasks;
using YesSql.Core.Indexes;

namespace YesSql.Core.Commands
{
    public abstract class DocumentCommand : IIndexCommand
    {
        protected static readonly PropertyInfo[] AllProperties = new PropertyInfo[]
        {
            typeof(Document).GetProperty("Type")
        };

        protected static readonly PropertyInfo[] AllKeys = new PropertyInfo[]
        {
            typeof(Document).GetProperty("Id")
        };
        public DocumentCommand(Document document)
        {
            Document = document;
        }

        public Document Document { get; }

        public abstract Task ExecuteAsync(IDbConnection connection, IDbTransaction transaction);
    }
}
