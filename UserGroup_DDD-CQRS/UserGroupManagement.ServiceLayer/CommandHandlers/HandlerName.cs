﻿namespace UserGroupManagement.ServiceLayer.CommandHandlers
{
    public class HandlerName
    {
        private readonly string _name;

        public HandlerName(string name)
        {
            _name = name;
        }

        public override string ToString()
        {
            return _name;
        }
    }
}