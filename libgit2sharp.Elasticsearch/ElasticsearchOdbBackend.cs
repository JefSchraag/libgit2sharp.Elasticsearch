﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NElasticsearch;
using NElasticsearch.Commands;
using NElasticsearch.Models;
using RestSharp.Extensions;
using GO = libgit2sharp.Elasticsearch.Models.GitObject;

namespace LibGit2Sharp.Elasticsearch
{
    public class ElasticsearchOdbBackend : OdbBackend
    {
        private readonly ConcurrentDictionary<string, GO> _cache = new ConcurrentDictionary<string, GO>();

        public bool EnableCaching { get; set; }

        private readonly string _indexName;
        protected ElasticsearchRestClient client;
        private const string GitObjectsType = "gitobject";

        public ElasticsearchOdbBackend(string elasticsearchUrl, string indexName)
        {
            _indexName = indexName;

            client = new ElasticsearchRestClient(elasticsearchUrl)
            {
                DefaultIndexName = indexName
            };

            // TODO mappings
        }

        protected override void Dispose()
        {
            base.Dispose();
        }

        public override int Read(ObjectId id, out Stream data, out ObjectType objectType)
        {
            GO obj;
            if (!EnableCaching || !_cache.TryGetValue(id.Sha, out obj) || obj.Data == null)
            {
                var response = ReadInternal(id.Sha, true);
                if (response == null || !response.found)
                {
                    objectType = ObjectType.Blob; data = null; // these will be ignored
                    return (int)ReturnCode.GIT_ENOTFOUND;
                }
                obj = response._source;
            }

            objectType = obj.Type;
            data = Allocate(obj.Length);
            var bytes = obj.GetDataAsByteArray();
            data.Write(bytes, 0, bytes.Length);

            return (int) ReturnCode.GIT_OK;
        }

        public override int ReadPrefix(string shortSha, out ObjectId id, out Stream data, out ObjectType objectType)
        {
            id = null;
            data = null;
            objectType = default(ObjectType);

            var q = new
            {
                query = new {constant_score = new {filter = new {prefix = new {Sha = shortSha}}}},
                from = 0, size = 1,
            };

            // TODO support user interrupts and return (int)ReturnCode.GIT_EUSER
            var results = client.Search<GO>(q, client.DefaultIndexName, GitObjectsType);
            if (results.hits.total == 0)
            {
                return (int) ReturnCode.GIT_ENOTFOUND;
            }

            if (results.hits.total == 1)
            {
                var obj = results.hits.hits[0]._source;
                id = new ObjectId(results.hits.hits[0]._id);
                if (EnableCaching)
                {
                    _cache.TryAdd(id.Sha, obj);
                }
                
                objectType = obj.Type;
                data = Allocate(obj.Length);
                var bytes = obj.GetDataAsByteArray();
                data.Write(bytes, 0, bytes.Length);

                return (int)ReturnCode.GIT_OK;
            }
            
            // More than 1 result returned, so the short sha is ambigous
            return (int)ReturnCode.GIT_EAMBIGUOUS;
        }

        public override int ReadHeader(ObjectId id, out int length, out ObjectType objectType)
        {
            GO obj;
            if (!EnableCaching || !_cache.TryGetValue(id.Sha, out obj))
            {
                var response = ReadInternal(id.Sha, false);
                if (response == null || !response.found)
                {
                    objectType = ObjectType.Blob; length = 0; // these will be ignored
                    return (int)ReturnCode.GIT_ENOTFOUND;
                }
                obj = response._source;
            }

            objectType = obj.Type;
            length = (int) obj.Length;

            return (int)ReturnCode.GIT_OK; 
        }

        private GetResponse<GO> ReadInternal(string sha, bool needsData)
        {
            // TODO implement needsData
            return client.Get<GO>(sha, GitObjectsType);
        }

        /// <summary>
        /// Writes a git object to the backend. Assumes libgit2 calls Exists before (which is indeed the case)
        /// </summary>
        /// <param name="id"></param>
        /// <param name="dataStream"></param>
        /// <param name="length"></param>
        /// <param name="objectType"></param>
        /// <returns></returns>
        public override int Write(ObjectId id, Stream dataStream, long length, ObjectType objectType)
        {
            var gitObject = new GO
            {
                Length = length,
                Type = objectType,
                Sha = id.Sha,
                Data = Convert.ToBase64String(dataStream.ReadAsBytes()),
            };

            client.Index(gitObject, id.Sha, GitObjectsType);
            if (EnableCaching) _cache.TryAdd(id.Sha, gitObject);            

            return (int)ReturnCode.GIT_OK;
        }

        public override int ReadStream(ObjectId id, out OdbBackendStream stream)
        {
            throw new NotImplementedException("ReadStream");
        }

        public override int WriteStream(long length, ObjectType objectType, out OdbBackendStream stream)
        {
            throw new NotImplementedException("WriteStream");
        }

        public override bool Exists(ObjectId id)
        {
            if (EnableCaching && _cache.ContainsKey(id.Sha))
                return true;

            var response = client.Get<GO>(id.Sha, GitObjectsType);
            if (EnableCaching && response != null && response.found)
            {
                _cache.TryAdd(id.Sha, response._source);
            }

            return response != null && response.found;
        }

        private int ForEachInternal(object query, ForEachCallback callback)
        {
            var curPage = 0;
            var pageSize = 10;
            var collectedResults = 0;

            // TODO better and more stable paging strategy
            while (true)
            {
                var q = new
                {
                    query = query,
                    from = curPage * pageSize,
                    size = pageSize,
                    fields = new string[] { }, // force returning only the ID
                };

                var results = client.Search<GO>(q, client.DefaultIndexName, GitObjectsType);
                if (results == null) // TODO smarter error handling
                {
                    return (int)ReturnCode.GIT_OK;
                }

                if (results.hits.hits.Select(hit => callback(new ObjectId(hit._id))).Any(ret => ret != (int)ReturnCode.GIT_OK))
                {
                    return (int)ReturnCode.GIT_EUSER;
                }

                collectedResults += results.hits.hits.Count;
                if (results.hits.hits.Count < pageSize || collectedResults == results.hits.total)
                    break;

                curPage++;
            }

            return (int)ReturnCode.GIT_OK;
        }

        private const int DefaultFetchAllPageSize = 30;
        public override int ForEach(ForEachCallback callback)
        {
            return ForEachInternal(new {match_all = new {}}, callback);
        }

        protected override OdbBackendOperations SupportedOperations
        {
            get
            {
                return OdbBackendOperations.Read |
                       OdbBackendOperations.Write |
                       OdbBackendOperations.ReadPrefix |
                       OdbBackendOperations.Exists |                   
                       OdbBackendOperations.ForEach;
            }
        }

        [Serializable]
        private class ObjectDescriptor
        {
            public ObjectType ObjectType { get; set; }
            public long Length { get; set; }
        }

        private class ElasticsearchOdbBackendWriteOnlyStream : OdbBackendStream
        {
            private readonly List<byte[]> _chunks = new List<byte[]>();

            private readonly ObjectType _type;
            private readonly long _length;

            public ElasticsearchOdbBackendWriteOnlyStream(ElasticsearchOdbBackend backend, ObjectType objectType, long length)
                : base(backend)
            {
                _type = objectType;
                _length = length;
            }

            public override bool CanRead
            {
                get
                {
                    return false;
                }
            }

            public override bool CanWrite
            {
                get
                {
                    return true;
                }
            }

            public override int Write(Stream dataStream, long length)
            {
                var buffer = new byte[length];

                int offset = 0, bytesRead;
                int toRead = Convert.ToInt32(length);

                do
                {
                    toRead -= offset;
                    bytesRead = dataStream.Read(buffer, offset, toRead);
                    offset += bytesRead;
                } while (bytesRead != 0);

                if (offset != (int)length)
                {
                    throw new InvalidOperationException(
                        string.Format("Too short buffer. {0} bytes were expected. {1} have been successfully read.",
                            length, bytesRead));
                }

                _chunks.Add(buffer);

                return (int)ReturnCode.GIT_OK;
            }

            public override int FinalizeWrite(ObjectId oid)
            {
                //TODO: Drop the check of the size when libgit2 #1837 is merged
                long totalLength = _chunks.Sum(chunk => chunk.Length);

                if (totalLength != _length)
                {
                    throw new InvalidOperationException(
                        string.Format("Invalid object length. {0} was expected. The "
                                      + "total size of the received chunks amounts to {1}.",
                                      _length, totalLength));
                }

                using (Stream stream = new FakeStream(_chunks, _length))
                {
                    Backend.Write(oid, stream, _length, _type);
                }

                return (int)ReturnCode.GIT_OK;
            }

            public override int Read(Stream dataStream, long length)
            {
                throw new NotImplementedException();
            }

            private class FakeStream : Stream
            {
                private readonly IList<byte[]> _chunks;
                private readonly long _length;
                private int currentChunk = 0;
                private int currentPos = 0;

                public FakeStream(IList<byte[]> chunks, long length)
                {
                    _chunks = chunks;
                    _length = length;
                }

                public override void Flush()
                {
                    throw new NotImplementedException();
                }

                public override long Seek(long offset, SeekOrigin origin)
                {
                    throw new NotImplementedException();
                }

                public override void SetLength(long value)
                {
                    throw new NotImplementedException();
                }

                public override int Read(byte[] buffer, int offset, int count)
                {
                    var totalCopied = 0;

                    while (totalCopied < count)
                    {
                        if (currentChunk > _chunks.Count - 1)
                        {
                            return totalCopied;
                        }

                        var toBeCopied = Math.Min(_chunks[currentChunk].Length - currentPos, count - totalCopied);

                        Buffer.BlockCopy(_chunks[currentChunk], currentPos, buffer, offset + totalCopied, toBeCopied);
                        currentPos += toBeCopied;
                        totalCopied += toBeCopied;

                        Debug.Assert(currentPos <= _chunks[currentChunk].Length);

                        if (currentPos == _chunks[currentChunk].Length)
                        {
                            currentPos = 0;
                            currentChunk++;
                        }
                    }

                    return totalCopied;
                }

                public override void Write(byte[] buffer, int offset, int count)
                {
                    throw new NotImplementedException();
                }

                public override bool CanRead
                {
                    get { return true; }
                }

                public override bool CanSeek
                {
                    get { throw new NotImplementedException(); }
                }

                public override bool CanWrite
                {
                    get { throw new NotImplementedException(); }
                }

                public override long Length
                {
                    get { return _length; }
                }

                public override long Position
                {
                    get { throw new NotImplementedException(); }
                    set { throw new NotImplementedException(); }
                }
            }
        }
    }
}