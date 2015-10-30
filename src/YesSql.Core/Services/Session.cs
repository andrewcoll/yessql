﻿using Dapper;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using YesSql.Core.Commands;
using YesSql.Core.Data;
using YesSql.Core.Indexes;
using YesSql.Core.Query;
using YesSql.Core.Serialization;
using YesSql.Core.Sql;
using YesSql.Core.Storage;

namespace YesSql.Core.Services
{
    public class Session : ISession
    {
        // Wether the Session should track the entities itself, in case the underlying
        // storage API doesn't do it
        private readonly IDbConnection _connection;
        private readonly ISqlDialect _dialect;
         
        private IDbTransaction _transaction;
        private IsolationLevel _isolationLevel;
        protected readonly IdentityMap _identityMap = new IdentityMap();
        private List<IIndexCommand> _commands = new List<IIndexCommand>();
        protected readonly bool _trackChanges;
        protected readonly IDictionary<IndexDescriptor, IList<MapState>> _maps;
        protected readonly HashSet<object> _saved = new HashSet<object>();
        protected readonly HashSet<object> _deleted = new HashSet<object>();
        protected readonly IDocumentStorage _storage;

        private Store _store;
        protected bool _cancel;
                
        public Session(IDocumentStorage storage, bool trackChanges, Store store)
        {
            _storage = storage;
            _store = store;
            _trackChanges = trackChanges;
            _isolationLevel = store._configuration.IsolationLevel;

            _maps = new Dictionary<IndexDescriptor, IList<MapState>>();

            _connection = _store._configuration.ConnectionFactory.CreateConnection();
            _dialect = SqlDialectFactory.For(_connection);
        }

        public void Save(object entity)
        {
            // don't add the object to the saved list if it's already tracked
            // unless we don't track automatically loaded objects
            if (!_trackChanges || !_identityMap.HasEntity(entity))
            {
                _saved.Add(entity);
            }
        }

        private async Task SaveEntityAsync(object obj)
        {
            if (obj == null)
            {
                throw new ArgumentNullException("obj");
            }

            var index = obj as Index;
            var document = obj as Document;

            if (document != null)
            {
                throw new ArgumentException("A document should not be saved explicitely");
            }
            else if (index != null)
            {
                throw new ArgumentException("An index should not be saved explicitely");
            }
            else
            {
                // if the object is not new, reload to get the old map
                int id;
                if(_identityMap.TryGetDocumentId(obj, out id))
                {
                    var oldObj = await _storage.GetAsync(id);
                    var oldDoc = await GetDocumentByIdAsync(id);

                    // Do nothing if the document hasn't been modified
                    // TODO: To prevent this check we could remove change tracking as a whole and have users
                    // use Update(entity), or have a NoTrack option in a session so that entities would still be 
                    // in the identity map but not saved on a Commit
                    if (!(String.Equals(JsonConvert.SerializeObject(obj), JsonConvert.SerializeObject(oldObj))))
                    {
                        // Update map index
                        MapDeleted(oldDoc, oldObj);
                        MapNew(oldDoc, obj);

                        // Save entity
                        await _storage.SaveAsync(id, obj);
                    }
                }
                else
                {
                    var doc = new Document { Type = obj.GetType().SimplifiedTypeName() };

                    await new CreateDocumentCommand(doc).ExecuteAsync(_connection, _transaction);
                    await _storage.SaveAsync(doc.Id, obj);

                    // Assign the Document id back to the entity
                    var accessor = _store.GetIdAccessor(obj.GetType(), "Id");
                    if (accessor != null)
                    {
                        accessor.Set(obj, doc.Id);
                    }

                    // Track the newly created object
                    _identityMap.Add(doc.Id, obj);

                    MapNew(doc, obj);
                }
            }
        }

        private async Task<Document> GetDocumentByIdAsync(int id)
        {
            var result = await _connection.QueryAsync<Document>("select * from Document where Id = @Id", new { Id = id }, _transaction);
            return result.FirstOrDefault();
        }

        public void Delete(object obj)
        {
            _deleted.Add(obj);
        }

        private async Task DeleteEntityAsync(object obj)
        {
            if (obj == null)
            {
                throw new ArgumentNullException("obj");
            }
            else if (obj is Index)
            {
                throw new ArgumentException("Can't call DeleteEntity on an Index");
            }
            else
            {
                // if the document has an Id property, use it to find the document
                var idInfo = obj.GetType().GetProperty("Id");
                if (idInfo == null)
                {
                    throw new InvalidOperationException("Could not delete object as it doesn't have an Id property");
                }

                var id = (int)idInfo.GetValue(obj, null);
                var doc = await GetDocumentByIdAsync(id);

                if (doc != null)
                {
                    await _storage.DeleteAsync(id);
                    _commands.Add(new DeleteDocumentCommand(doc));

                    // Untrack the deleted object
                    _identityMap.Remove(id, obj);

                    // Update impacted indexes
                    MapDeleted(doc, obj);
                }
            }
        }

        public async Task<IEnumerable<T>> GetAsync<T>(IEnumerable<int> ids) where T : class
        {
            var result = new List<T>();

            
            if (ids == null || !ids.Any())
            {
                return result;
            }

            // Are all the objects already in cache?
            IEnumerable<object> cached = ids.Select(id =>
            {
                object document;
                if (_identityMap.TryGetEntityById(id, out document))
                {
                    return document;
                }
                return null;
            });

            if (!cached.Any(x => x == null))
            {
                foreach (T item in cached)
                {
                    result.Add(item);
                }
            }

            // Some documents might not be in cache, load all of them from storage, then resolve local maps
            var items = (await _storage.GetAsync<T>(ids)).ToArray();

            // if the document has an Id property, set it back
            var accessor = _store.GetIdAccessor(typeof(T), "Id");

            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                var id = ids.ElementAt(i);

                object document;

                if (_identityMap.TryGetEntityById(id, out document))
                {    
                    result.Add((T)document);
                }
                else
                {
                    accessor.Set(item, id);

                    // track the loaded object
                    _identityMap.Add(id, item);

                    result.Add(item);
                }
            }

            return result;
        }
        
        public IQueryable<TIndex> QueryIndex<TIndex>() where TIndex : Index
        {
            Demand();

            // Commit any pending changes before doing a query (auto-flush)
            CommitAsync().Wait();
            return null;
            //return _dbContext.Set<TIndex>();
        }

        public IQuery QueryAsync()
        {
            Demand();

            // Commit any pending changes before doing a query (auto-flush)
            CommitAsync().Wait();

            return new DefaultQuery(_connection, _transaction, this);
        }

        public void Dispose()
        {
            try
            {
                if (!_cancel)
                {
                    // execute pending commands
                    CommitAsync().Wait();

                    if (_transaction != null)
                    {
                        _transaction.Commit();
                    }
                }
                else
                {
                    if (_transaction != null)
                    {
                        _transaction.Rollback();
                    }
                }
            }
            finally
            {
                if (_transaction != null)
                {
                    _transaction.Dispose();
                }

                if (_connection != null && _store._configuration.ConnectionFactory.Disposable)
                {
                    _connection.Dispose();
                }
            }
        }

        public async Task CommitAsync()
        {
            Demand();

            // saving all tracked objects
            if (_trackChanges)
            {
                foreach (var obj in _identityMap.GetAll())
                {
                    if (!_deleted.Contains(obj))
                    {
                        await SaveEntityAsync(obj);
                    }
                }
            }

            // saving all pending objects
            foreach (var obj in _saved)
            {
                await SaveEntityAsync(obj);
            }
            _saved.Clear();

            // deleting all pending objects
            foreach (var obj in _deleted)
            {
                await DeleteEntityAsync(obj);
            }
            _deleted.Clear();

            // compute all reduce indexes
            await ReduceAsync();

            foreach(var command in _commands)
            {
                await command.ExecuteAsync(_connection, _transaction);
            }

            _commands.Clear();
        }
        
        private async Task ReduceAsync()
        {
            // loop over each Indexer used by new objects
            foreach (var descriptor in _maps.Keys)
            {
                // if the descriptor has no reduce behavior, ignore it
                if (descriptor.Reduce == null)
                    continue;

                if (descriptor.GroupKey == null)
                {
                    throw new InvalidOperationException(
                        "A map/reduce index must declare at least one property with a GroupKey attribute: " +
                        descriptor.Type.FullName);
                }

                // a groupping method for the current descriptor
                var descriptorGroup = GetGroupingMetod(descriptor);

                // list all available grouping keys in the current set
                var allKeysForDescriptor =
                    _maps[descriptor].Select(x => x.Map).Select(descriptorGroup).Distinct().ToArray();

                // reduce each group, will result in one Reduced index per group
                foreach (var currentKey in allKeysForDescriptor)
                {
                    // group all mapped indexes 
                    var newMapsGroup =
                        _maps[descriptor].Where(x => x.State == MapStates.New).Select(x => x.Map).Where(
                            x => descriptorGroup(x).Equals(currentKey)).ToArray();
                    var deletedMapsGroup =
                        _maps[descriptor].Where(x => x.State == MapStates.Delete).Select(x => x.Map).Where(
                            x => descriptorGroup(x).Equals(currentKey)).ToArray();
                    var updatedMapsGroup =
                        _maps[descriptor].Where(x => x.State == MapStates.Update).Select(x => x.Map).Where(
                            x => descriptorGroup(x).Equals(currentKey)).ToArray();

                    // todo: if an updated object got his Key changed, then apply a New to the new value group
                    // and a Delete to the old value group. Otherwise apply Update to the current value group

                    Index index = null;

                    if (newMapsGroup.Any())
                    {
                        // reducing an already groupped set (technically the reduction should contain the grouping step, but by design ...)
                        index = descriptor.Reduce(newMapsGroup.GroupBy(descriptorGroup).First());

                        if (index == null)
                        {
                            throw new InvalidOperationException(
                                "The reduction on a grouped set shoud have resulted in a unique result"
                                );
                        }
                    }

                    ReduceIndex dbIndex = await ReduceForAsync(descriptor, currentKey);

                    // if index present in db and new objects, reduce them
                    if (dbIndex != null && index != null)
                    {
                        // reduce over the two objects
                        var reductions = new[] { dbIndex, index };

                        var grouppedReductions = reductions.GroupBy(descriptorGroup).SingleOrDefault();

                        if (grouppedReductions == null)
                        {
                            throw new InvalidOperationException(
                                "The grouping on the db and in memory set shoud have resulted in a unique result");
                        }

                        index = descriptor.Reduce(grouppedReductions);

                        if (index == null)
                        {
                            throw new InvalidOperationException(
                                "The reduction on a grouped set shoud have resulted in a unique result");
                        }
                    }
                    else if (dbIndex != null)
                    {
                        index = dbIndex;
                    }

                    if (index != null)
                    {
                        // are there any deleted object for this descriptor/group ?
                        if (deletedMapsGroup.Any())
                        {
                            index = descriptor.Delete(index, deletedMapsGroup.GroupBy(descriptorGroup).First());
                            // At this point, index can be null if the reduction returned a null index from Delete handler
                        }

                        // are there any updated object for this descriptor/group ?
                        if (updatedMapsGroup.Any())
                        {
                            index = descriptor.Update(index, updatedMapsGroup.GroupBy(descriptorGroup).First());
                        }
                    }

                    var deletedDocumentIds = deletedMapsGroup.SelectMany(x => x.GetRemovedDocuments().Select(d => d.Id));
                    var addedDocumentIds = newMapsGroup.SelectMany(x => x.GetAddedDocuments().Select(d => d.Id));

                    if (dbIndex != null)
                    {
                        if (index == null)
                        {
                            _commands.Add(new DeleteReduceIndexCommand(dbIndex));
                        }
                        else
                        {
                            index.Id = dbIndex.Id;

                            // Update both new and deleted linked documents
                            _commands.Add(new UpdateIndexCommand(index, addedDocumentIds, deletedDocumentIds));
                        }
                    }
                    else 
                    {
                        if (index != null)
                        {
                            // The index is new
                            _commands.Add(new CreateIndexCommand(index, addedDocumentIds));
                        }
                    }
                }
            }
        }

        public async Task<ReduceIndex> ReduceForAsync(IndexDescriptor descriptor, object currentKey)
        {
            var name = descriptor.IndexType.Name;
            var sql = $"select * from {name} where {descriptor.GroupKey.Name} = @currentKey";

            var index = await _connection.QueryAsync(descriptor.IndexType, sql, new { currentKey });
            return index.FirstOrDefault() as ReduceIndex;
        }

        /// <summary>
        /// Creates a Func{IIndex, object}; dynamically, based on GroupKey attributes
        /// this function will be used as the keySelector for Linq.Grouping
        /// </summary>
        private Func<Index, object> GetGroupingMetod(IndexDescriptor descriptor)
        {
            return _store.GroupMethods.GetOrAdd(descriptor.Type, (Type key) =>
            {
                // IIndex i => i
                var instance = Expression.Parameter(typeof(Index), "i");
                // i => ((TIndex)i)
                var convertInstance = Expression.Convert(instance, descriptor.GroupKey.DeclaringType);
                // i => ((TIndex)i).{Property}
                var property = Expression.Property(convertInstance, descriptor.GroupKey);
                // i => (object)(((TIndex)i).{Property})
                var convert = Expression.Convert(property, typeof(object));

                return Expression.Lambda<Func<Index, object>>(convert, instance).Compile();
            });
        }

        private void MapNew(Document document, object obj)
        {
            foreach (var descriptor in _store.Describe(obj.GetType()))
            {
                var mapped = descriptor.Map(obj);

                foreach (var index in mapped)
                {
                    index.AddDocument(document);

                    // if the mapped elements are not meant to be reduced,
                    // then save them in db, as index
                    if (descriptor.Reduce == null)
                    {
                        if (index.Id == 0)
                        {
                            _commands.Add(new CreateIndexCommand(index, Enumerable.Empty<int>()));
                        }
                        else
                        {
                            _commands.Add(new UpdateIndexCommand(index, Enumerable.Empty<int>(), Enumerable.Empty<int>()));
                        }
                    }
                    else
                    {
                        // save for later reducing
                        IList<MapState> listmap;
                        if (!_maps.TryGetValue(descriptor, out listmap))
                        {
                            _maps.Add(descriptor, listmap = new List<MapState>());
                        }

                        listmap.Add(new MapState(index, MapStates.New));
                    }
                }
            }
        }

        /// <summary>
        /// Update map and reduce indexes when an entity is deleted.
        /// </summary>
        private void MapDeleted(Document document, object obj)
        {
            foreach (var descriptor in _store.Describe(obj.GetType()))
            {
                // If the mapped elements are not meant to be reduced, delete
                if (descriptor.Reduce == null || descriptor.Delete == null)
                {
                    _commands.Add(new DeleteMapIndexCommand(descriptor.IndexType, document.Id));
                }
                else
                {
                    var mapped = descriptor.Map(obj);

                    foreach (var index in mapped)
                    {
                        // save for later reducing
                        IList<MapState> listmap;
                        if (!_maps.TryGetValue(descriptor, out listmap))
                        {
                            _maps.Add(descriptor, listmap = new List<MapState>());
                        }

                        listmap.Add(new MapState(index, MapStates.Delete));
                        index.RemoveDocument(document);
                    }
                }
            }
        }

        /// <summary>
        /// Initializes a new transaction if none has been yet
        /// </summary>
        protected void Demand()
        {
            if (_transaction == null)
            {
                if (_connection.State == ConnectionState.Closed)
                {
                    _connection.Open();
                }

                _transaction = _connection.BeginTransaction(_isolationLevel);
            }
        }

        public void Cancel()
        {
            _cancel = true;
        }

        public ISession IsolationLevel(IsolationLevel isolationLevel)
        {
            _isolationLevel = isolationLevel;
            return this;
        }
    }

}
