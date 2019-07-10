#region Licence
/* The MIT License (MIT)
Copyright © 2019 Ian Cooper <ian_hammond_cooper@yahoo.co.uk>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the “Software”), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE. */
#endregion
using System;
using System.Collections.Generic;
using System.Linq;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.Model;

namespace Paramore.Brighter.Outbox.DynamoDB
{
    /// <summary>
    /// We don't have any non-scalar types here as the .NET Low-Level API does not support creating them
    /// You can have them on a class, as the property will still be persisted to an appropriate field by the object model.
    /// Or you can create with HTTP or CLI. But the only types .NET allow you to use with the Low-Level API are B, N and S
    /// So we only look for Scalar attributes, Hash and Range Keys to create the statement.
    /// </summary>
    public class DynamoDbTableFactory
    {
        public CreateTableRequest GenerateCreateTableMapper<T>()
        {
            var docType = typeof(T);
            var tableAttribute = docType.GetCustomAttributesData().FirstOrDefault(attr => attr.AttributeType == typeof(DynamoDBTableAttribute));
            if (tableAttribute == null)
                throw new InvalidOperationException("Types to be mapped must have the DynamoDbTableAttribute");

            string tableName = tableAttribute.ConstructorArguments.Count == 0 ? 
                docType.Name : (string)tableAttribute.ConstructorArguments.FirstOrDefault().Value;
;

            //hash key
            var hashKey = from prop in docType.GetProperties()
                from attribute in prop.GetCustomAttributesData()
                where attribute.AttributeType == typeof(DynamoDBHashKeyAttribute)
                select new KeySchemaElement(prop.Name, KeyType.HASH);

            var rangeKey = from prop in docType.GetProperties()
                from attribute in prop.GetCustomAttributesData()
                where attribute.AttributeType == typeof(DynamoDBRangeKeyAttribute)
                select new KeySchemaElement(prop.Name, KeyType.RANGE);

            var index = hashKey.Concat(rangeKey);
            
            //global secondary indexes
            var gsiMap = new Dictionary<string, GlobalSecondaryIndex>();

            var gsiHashKeyResults = from prop in docType.GetProperties()
                from attribute in prop.GetCustomAttributesData()
                where attribute.AttributeType == typeof(DynamoDBGlobalSecondaryIndexHashKeyAttribute)
                select new {prop, attribute};
            
            foreach (var gsiHashKeyResult in gsiHashKeyResults)
            {
                var gsi = new GlobalSecondaryIndex();
                gsi.IndexName = gsiHashKeyResult.attribute.ConstructorArguments.Count == 0
                    ? gsiHashKeyResult.prop.Name
                    : (string)gsiHashKeyResult.attribute.ConstructorArguments.FirstOrDefault().Value;

                var gsiHashKey = new KeySchemaElement(gsiHashKeyResult.prop.Name, KeyType.HASH);
                gsi.KeySchema.Add(gsiHashKey);
                
                gsiMap.Add(gsi.IndexName, gsi);
            }

            var gsiRangeKeyResults = from prop in docType.GetProperties()
                from attribute in prop.GetCustomAttributesData()
                where attribute.AttributeType == typeof(DynamoDBGlobalSecondaryIndexRangeKeyAttribute)
                select new {prop, attribute};

            foreach (var gsiRangeKeyResult in gsiRangeKeyResults)
            {
                var indexName = gsiRangeKeyResult.attribute.ConstructorArguments.Count == 0
                    ? gsiRangeKeyResult.prop.Name
                    : (string)gsiRangeKeyResult.attribute.ConstructorArguments.FirstOrDefault().Value;
                
                if (!gsiMap.ContainsKey(indexName))
                    throw new InvalidOperationException($"The global secondary index {gsiRangeKeyResult.prop.Name} lacks a hash key");

                var entry = gsiMap[indexName];
                var gsiRangeKey = new KeySchemaElement(gsiRangeKeyResult.prop.Name, KeyType.RANGE);
                entry.KeySchema.Add(gsiRangeKey);
            }

            //local secondary indexes
            var lsiList = new List<LocalSecondaryIndex>();

            var lsiRangeKeyResults = from prop in docType.GetProperties()
                from attribute in prop.GetCustomAttributesData()
                where attribute.AttributeType == typeof(DynamoDBLocalSecondaryIndexRangeKeyAttribute)
                select new {prop, attribute};

            foreach (var lsiRangeKeyResult in lsiRangeKeyResults)
            {
                var indexName = lsiRangeKeyResult.attribute.ConstructorArguments.Count == 0
                    ? lsiRangeKeyResult.prop.Name
                    : (string)lsiRangeKeyResult.attribute.ConstructorArguments.FirstOrDefault().Value;
                
                var lsi = new LocalSecondaryIndex();
                lsi.IndexName = indexName;
                lsi.KeySchema.Add(new KeySchemaElement(lsiRangeKeyResult.prop.Name, KeyType.RANGE));
                lsiList.Add(lsi);
            }
            
            //attributes
            var fields = from prop in docType.GetProperties()
                from attribute in prop.GetCustomAttributesData()
                where attribute.AttributeType == typeof(DynamoDBPropertyAttribute)
                select new {prop, attribute};

            var attributeDefinitions = new List<AttributeDefinition>();
            foreach (var item in fields)
            {
                string attributeName = item.attribute.ConstructorArguments.Count == 0 ? 
                    item.prop.Name : (string)item.attribute.ConstructorArguments.FirstOrDefault().Value;
                    
                attributeDefinitions.Add(new AttributeDefinition(attributeName, GetDynamoDbType(item.prop.PropertyType)));
            }
                
            var createTableRequest = new CreateTableRequest(tableName, index.ToList());
            createTableRequest.AttributeDefinitions.AddRange(attributeDefinitions);
            createTableRequest.GlobalSecondaryIndexes.AddRange(gsiMap.Select(entry => entry.Value));
            createTableRequest.LocalSecondaryIndexes.AddRange(lsiList);
            return createTableRequest;
        }

        // We treat all primitive types as a number
        // Then we test for a string, and treat that explicitly as a string
        // If not we look for a byte array and treat it as binary
        // Everything else is unsupported in .NET
        private ScalarAttributeType GetDynamoDbType(Type propertyType)
        {
            if (propertyType.IsPrimitive)
            {
                return ScalarAttributeType.N;
            }

            if (propertyType == typeof(string))
            {
                return ScalarAttributeType.S;
            }

            if (propertyType == typeof(byte[]))
            {
                return ScalarAttributeType.B;
            }

            throw new NotSupportedException($"We can't convert {propertyType.Name} to a DynamoDb type. Avoid marking as an attribute and see if the lib can figure it out");
        }
    }
}