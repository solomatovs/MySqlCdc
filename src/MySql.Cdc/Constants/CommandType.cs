namespace MySql.Cdc.Constants
{
    /// <summary>
    /// Command types are included in body headers to identify the commands
    /// </summary>
    public enum CommandType : byte
    {
        SLEEP,
        QUIT,
        INIT_DB,
        QUERY,
        FIELD_LIST,
        CREATE_DB,
        DROP_DB,
        REFRESH,
        SHUTDOWN,
        STATISTICS,
        PROCESS_INFO,
        CONNECT,
        PROCESS_KILL,
        DEBUG,
        PING,
        TIME,
        DELAYED_INSERT,
        CHANGE_USER,
        BINLOG_DUMP,

        REGISTER_SLAVE = 21
    }
}
