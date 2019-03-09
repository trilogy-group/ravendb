﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Session;
using Raven.Client.Http;
using Raven.Client.Json;
using Raven.Client.Util;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Operations.CompareExchange
{
    public class PutCompareExchangeValueOperation<T> : IOperation<CompareExchangeResult<T>>
    {
        private readonly string _key;
        private readonly T _value;
        private readonly long _index;

        public PutCompareExchangeValueOperation(string key, T value, long index)
        {
            _key = key;
            _value = value;
            _index = index;
        }

        public RavenCommand<CompareExchangeResult<T>> GetCommand(IDocumentStore store, DocumentConventions conventions, JsonOperationContext context, HttpCache cache)
        {
            return new PutCompareExchangeValueCommand(_key, _value, _index, conventions);
        }

        private class PutCompareExchangeValueCommand : RavenCommand<CompareExchangeResult<T>>
        {
            private readonly string _key;
            private readonly T _value;
            private readonly long _index;
            private readonly DocumentConventions _conventions;

            public PutCompareExchangeValueCommand(string key, T value, long index, DocumentConventions conventions = null)
            {
                if (string.IsNullOrEmpty(key))
                    throw new ArgumentNullException(nameof(key), "The key argument must have value");
                if (index < 0)
                    throw new InvalidDataException("Index must be a non-negative number");

                _key = key;
                _value = value;
                _index = index;
                _conventions = conventions ?? DocumentConventions.Default;
            }

            public override bool IsReadRequest => false;

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/databases/{node.Database}/cmpxchg?key={_key}&index={_index}";
                var djv = new DynamicJsonValue
                {
                    ["Object"] = EntityToBlittable.ConvertToBlittableIfNeeded(_value, _conventions, ctx, _conventions.CreateSerializer(), documentInfo: null, removeIdentityProperty: false)
                };
               var blittable = ctx.ReadObject(djv,_key);

                var request = new HttpRequestMessage
                {
                    Method = HttpMethods.Put,
                    Content = new BlittableJsonContent(stream =>
                    {
                        ctx.Write(stream, blittable);
                    })
                };
                return request;
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
            {
                Result = CompareExchangeResult<T>.ParseFromBlittable(response, _conventions);
            }
        }
    }
}
