﻿using Sparrow.Json;
using System.Collections.Generic;

namespace Raven.Server.Documents.Indexes.MapReduce.Auto
{
    public class AggregatedDocuments : AggregationResult
    {
        private readonly List<Document> _outputs;

        public AggregatedDocuments(List<Document> results)
        {
            _outputs = results;
        }

        public override int Count => _outputs.Count;

        public override IEnumerable<object> GetOutputs()
        {
            return _outputs;
        }

        public override IEnumerable<BlittableJsonReaderObject> GetOutputsToStore()
        {
            foreach (var output in _outputs)
            {
                yield return output.Data;
            }
        }

        public override void Dispose()
        {
            foreach (var item in _outputs)
            {
                item.Data.Dispose();
            }
        }
    }

}
