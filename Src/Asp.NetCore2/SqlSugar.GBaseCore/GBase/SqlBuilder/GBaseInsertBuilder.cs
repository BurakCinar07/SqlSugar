﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SqlSugar.GBase
{
    public class GBaseInsertBuilder:InsertBuilder
    {
        private string _getAutoGeneratedKeyValueSql = " select case when dbinfo('serial8') <> 0 then dbinfo('serial8') when dbinfo('bigserial') <> 0 then dbinfo('bigserial') else dbinfo('sqlca.sqlerrd1') end from dual";

        public override string SqlTemplateBatch
        {
            get
            {
                return "INSERT  into {0} ({1})";
            }
        }
        public override string SqlTemplate
        {
            get
            {
                if (IsReturnIdentity)
                {
                    return @"INSERT INTO {0} 
           ({1})
     VALUES
           ({2})"+UtilConstants.ReplaceCommaKey.Replace("{","").Replace("}", "") + _getAutoGeneratedKeyValueSql;
                }
                else
                {
                    return @"INSERT INTO {0} 
           ({1})
     VALUES
           ({2}) ";
                }
            }
        }
        public override string GetTableNameString
        {
            get
            {
                var result = Builder.GetTranslationTableName(EntityInfo.EntityName);
                if (this.AsName.HasValue())
                {
                    result = Builder.GetTranslationTableName(this.AsName);
                }
                result += UtilConstants.Space;
                if (this.TableWithString.HasValue())
                {
                    result += TableWithString + UtilConstants.Space;
                }
                return result;
            }
        }
        public override string ToSqlString()
        {
            if (IsNoInsertNull)
            {
                DbColumnInfoList = DbColumnInfoList.Where(it => it.Value != null).ToList();
            }
            var groupList = DbColumnInfoList.GroupBy(it => it.TableId).ToList();
            var isSingle = groupList.Count() == 1;
            string columnsString = string.Join(",", groupList.First().Select(it => Builder.GetTranslationColumnName(it.DbColumnName)));
            if (isSingle)
            {
                // copy the SugarColumn(ColumnDataType) to  parameter.TypeName
                var bigObjectColumns = groupList.ToList().First().Where(o => !string.IsNullOrEmpty(o.DataType)).ToList();
                foreach (var column in bigObjectColumns)
                {
                    var columnName = Builder.SqlParameterKeyWord + column.DbColumnName;
                    var param = this.Parameters.Where(o => string.Compare(o.ParameterName, columnName) == 0).FirstOrDefault();
                    if (param.HasValue())
                    {
                        param.TypeName = column.DataType.ToLower();
                    }
                }
                string columnParametersString = string.Join(",", this.DbColumnInfoList.Select(it => base.GetDbColumn(it, Builder.SqlParameterKeyWord + it.DbColumnName)));
                return string.Format(SqlTemplate, GetTableNameString, columnsString, columnParametersString);
            }
            else
            {
                StringBuilder batchInsetrSql = new StringBuilder();
                int pageSize = groupList.Count;
                int pageIndex = 1;
                int totalRecord = groupList.Count;
                int pageCount = (totalRecord + pageSize - 1) / pageSize;
                Boolean hasBigObjectColumn = groupList.First().Where(o => UtilMethods.HasBigObjectColumn(o)).Count() > 0;

                if (this.Parameters != null) this.Parameters.Clear();

                while (pageCount >= pageIndex)
                {
                    if (!hasBigObjectColumn)
                    {
                        batchInsetrSql.AppendFormat(SqlTemplateBatch, GetTableNameString, columnsString);
                        batchInsetrSql.AppendFormat("SELECT * FROM (");
                    }
                    int i = 0;
                    foreach (var columns in groupList.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToList())
                    {
                        var isFirst = i == 0;
                        if (!hasBigObjectColumn)
                        {
                            if (!isFirst)
                            {
                                batchInsetrSql.Append(SqlTemplateBatchUnion);
                            }
                            batchInsetrSql.Append("\r\n SELECT " + string.Join(",", columns.Select(it => string.Format(SqlTemplateBatchSelect, base.GetDbColumn(it, FormatValue(it.Value)), Builder.GetTranslationColumnName(it.DbColumnName)))) + " from dual");
                        }
                        else
                        {
                            // has big object column.
                            if (!isFirst)
                            {
                                batchInsetrSql.Append(UtilConstants.ReplaceCommaKey.Replace("{", "").Replace("}", " "));
                            }

                            // generate batch insert sqlstatements, which has big object datatype column.
                            batchInsetrSql.AppendFormat(SqlTemplateBatch, GetTableNameString, columnsString);
                            batchInsetrSql.AppendFormat(" Values (");
                            batchInsetrSql.AppendFormat(string.Join(",", columns.Select(it => base.GetDbColumn(it, i + Builder.SqlParameterKeyWord + it.DbColumnName + i))));
                            batchInsetrSql.AppendFormat(");");

                            // multiple rows data would be added to the parameters
                            this.AddBatchInsertParameters(columns, i);
                        }
                        ++i;
                    }
                    pageIndex++;
                    if (!hasBigObjectColumn)
                    {
                        batchInsetrSql.Append(") temp1\r\n;\r\n");
                    }
                }
                var result = batchInsetrSql.ToString();
                //if (this.Context.CurrentConnectionConfig.DbType == DbType.GBase)
                //{
                //    result += "";
                //}
                return result;
            }
        }
        public override object FormatValue(object value)
        {
            var n = "";
            if (value == null)
            {
                return "NULL";
            }
            else
            {
                var type = UtilMethods.GetUnderType(value.GetType());
                if (type == UtilConstants.DateType)
                {
                    return GetDateTimeString(value);
                }
                else if (value is DateTimeOffset)
                {
                    return GetDateTimeOffsetString(value);
                }
                else if (type == UtilConstants.ByteArrayType)
                {
                    string bytesString = "0x" + BitConverter.ToString((byte[])value).Replace("-", "");
                    return bytesString;
                }
                else if (type.IsEnum())
                {
                    if (this.Context.CurrentConnectionConfig.MoreSettings?.TableEnumIsString == true)
                    {
                        return value.ToSqlValue();
                    }
                    else
                    {
                        return Convert.ToInt64(value);
                    }
                }
                else if (type == UtilConstants.BoolType)
                {
                    return string.Format("CAST({0} AS boolean)", value.ObjToBool()?1:0) ;
                }
                else if (type == UtilConstants.IntType || 
                    type == UtilConstants.LongType ||
                    type == UtilConstants.ShortType ||
                    type == UtilConstants.FloatType ||
                    type == UtilConstants.DobType)
                {
                    return value;
                }
                else
                {
                    return n + "'" + value + "'";
                }
            }
        }
        private object GetDateTimeOffsetString(object value)
        {
            var date = UtilMethods.ConvertFromDateTimeOffset((DateTimeOffset)value);
            if (date < UtilMethods.GetMinDate(this.Context.CurrentConnectionConfig))
            {
                date = UtilMethods.GetMinDate(this.Context.CurrentConnectionConfig);
            }
            return "'" + date.ToString("yyyy-MM-dd HH:mm:ss.fff") + "'";
        }

        private object GetDateTimeString(object value)
        {
            var date = value.ObjToDate();
            if (date < UtilMethods.GetMinDate(this.Context.CurrentConnectionConfig))
            {
                date = UtilMethods.GetMinDate(this.Context.CurrentConnectionConfig);
            }
            return "'" + date.ToString("yyyy-MM-dd HH:mm:ss.fff") + "'";
        }

        // the method below is copied from InsertableHelper.cs
        private static void ArrayNull(DbColumnInfo item, SugarParameter parameter)
        {
            if (item.PropertyType.IsIn(typeof(Guid[]), typeof(Guid?[])))
            {
                parameter.DbType = System.Data.DbType.Guid;
            }
            else if (item.PropertyType.IsIn(typeof(int[]), typeof(int?[])))
            {
                parameter.DbType = System.Data.DbType.Int32;
            }
            else if (item.PropertyType.IsIn(typeof(long[]), typeof(long?[])))
            {
                parameter.DbType = System.Data.DbType.Int64;
            }
            else if (item.PropertyType.IsIn(typeof(short[]), typeof(short?[])))
            {
                parameter.DbType = System.Data.DbType.Int16;
            }
        }

        // The code in AddBatchInsertParameters are copied from PreToSql() in InsertableHelper.cs
        private void AddBatchInsertParameters(IGrouping<int, DbColumnInfo> columns, int rowIndex)
        {
            var isDic = this.EntityInfo.DbTableName.StartsWith("Dictionary`");
            foreach (var item in columns)
            {
                if (this.Parameters == null) this.Parameters = new List<SugarParameter>();
                var paramters = new SugarParameter(rowIndex + Builder.SqlParameterKeyWord + item.DbColumnName + rowIndex, item.Value, item.PropertyType);
                if (IsNoInsertNull && paramters.Value == null)
                {
                    continue;
                }
                if (item.SqlParameterDbType is Type)
                {
                    continue;
                }
                if (item.IsJson)
                {
                    paramters.IsJson = true;
                    Builder.ChangeJsonType(paramters);
                }
                if (item.IsArray)
                {
                    paramters.IsArray = true;
                    if (item.Value == null || item.Value == DBNull.Value)
                    {
                        ArrayNull(item, paramters);
                    }

                }
                if (item.Value == null && isDic)
                {
                    var type = this.Builder.GetNullType(this.GetTableNameString, item.DbColumnName);
                    if (type != null)
                    {
                        paramters = new SugarParameter(rowIndex + this.Builder.SqlParameterKeyWord + item.DbColumnName + rowIndex, item.Value, type);
                    }
                }
                if (!string.IsNullOrEmpty(item.DataType))
                {
                    paramters.TypeName = item.DataType.ToLower();
                }
                this.Parameters.Add(paramters);
            }
        }
    }
}