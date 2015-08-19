﻿using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using RealmNet.Interop;

namespace RealmNet
{
    public class Realm : Handled
    {
        public static ICoreProvider ActiveCoreProvider;

        public static Realm GetInstance(string path = null)
        {
            return new Realm(ActiveCoreProvider, path);
        }

        private readonly ICoreProvider _coreProvider;

        private ISharedGroupHandle SharedGroupHandle => Handle as ISharedGroupHandle;
        private IGroupHandle _transactionGroupHandle;

        private Realm(ICoreProvider coreProvider, string path) 
        {
            _coreProvider = coreProvider;
            SetHandle(coreProvider.CreateSharedGroup(path), false);
        }

        public T CreateObject<T>() where T : RealmObject
        {
            return (T)CreateObject(typeof(T));
        }

        public object CreateObject(Type objectType)
        {
            if (!_coreProvider.HasTable(_transactionGroupHandle, objectType.Name))
                CreateTableFor(objectType);

            var result = (RealmObject)Activator.CreateInstance(objectType);
            var rowIndex = _coreProvider.AddEmptyRow(_transactionGroupHandle, objectType.Name);

            result._Manage(this, _coreProvider, rowIndex);

            return result;
        }

        private void CreateTableFor(Type objectType)
        {
            var tableName = objectType.Name;

            if (!objectType.GetTypeInfo().GetCustomAttributes(typeof(WovenAttribute), true).Any())
                Debug.WriteLine("WARNING! The type " + tableName + " is a RealmObject but it has not been woven.");

            _coreProvider.AddTable(_transactionGroupHandle, tableName);

            var propertiesToMap = objectType.GetTypeInfo().DeclaredProperties.Where(p => p.CustomAttributes.All(a => a.AttributeType != typeof (IgnoreAttribute)));
            foreach (var p in propertiesToMap)
            {
                var propertyName = p.Name;
                var mapToAttribute = p.CustomAttributes.FirstOrDefault(a => a.AttributeType == typeof(MapToAttribute));
                if (mapToAttribute != null)
                    propertyName = ((string)mapToAttribute.ConstructorArguments[0].Value);
                
                var columnType = p.PropertyType;
                _coreProvider.AddColumnToTable(_transactionGroupHandle, tableName, propertyName, columnType);
            }
        }

        public RealmQuery<T> All<T>()
        {
            return new RealmQuery<T>(this, _coreProvider);
        }

        internal TransactionState State
        {
            get { return SharedGroupHandle.State; }
//            set { SharedGroupHandle.State = value; }
        }

        internal IGroupHandle TransactionGroupHandle => _transactionGroupHandle;


        //this is the only place where a read transaction can be initiated
        /// <summary>
        /// initiates a Read transaction by returning a Transaction object that is also a Group object.
        /// The group object will represent the database as it was when BeginRead was executed and will stay in that state
        /// even as the database is being changed by other processes, it is a snapshot.
        /// The group returned is read only, it is illegal to make changes to it.
        /// Transaction.Commit() will dispose of the underlying structures maintaining the readonly view of the database, 
        /// the structures will also be disposed if the transaction goes out of scope.
        /// Calling commit() as soon as you are done with the transaction will free up memory a little faster than relying on dispose
        /// </summary>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
        public Transaction BeginRead()
        {
           ValidateNotInTransaction();
           _transactionGroupHandle = SharedGroupHandle.StartTransaction(TransactionState.Read);//SGH.StartTransaction is atomic reg. transaction state and calling core
           if (_transactionGroupHandle.IsInvalid)
               throw new InvalidOperationException("Cannot start Read Transaction, probably an IO error with the SharedGroup file");
           return new Transaction(_transactionGroupHandle, this);
        }

        //this is the only place where a write transaction can be initiated
        /// <summary>
        /// Initiate a write transaction by returning a Transaction that is also a Group.
        /// You can then modify the tables in the group exclusively. Your modifications will not be visible to readers simultaneously 
        /// reading data from the database - until you do transaction.commit(). At that point any new readers will see the updated database,
        /// existing readers will continue to see their copy of the database as it was when they started their read transaction.
        /// Only one writer can exist at a time, so if you call BeginWrite the function might wait until the prior writer do a commit()
        /// </summary>
        /// <returns>Transaction object that inherits from Group and gives read/write acces to all tables in the group</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
        public Transaction BeginWrite()
        {
            ValidateNotInTransaction();
            _transactionGroupHandle = SharedGroupHandle.StartTransaction(TransactionState.Write);
            if (_transactionGroupHandle.IsInvalid)
                throw new InvalidOperationException("Cannot start Write Transaction, probably an IO error with the SharedGroup file");
            return new Transaction(_transactionGroupHandle, this);
        }

        //called by the user directly or indirectly via dispose. Finalizer in SharedGroupHandle might also end
        /// <summary>
        /// defaults to true
        /// if Isvalid is false something fatal has happened with the shared group wrapper        
        /// </summary>
        public bool IsValid
        {
            get { return (!SharedGroupHandle.IsInvalid); }
            private set
            {
                //ignore calls where we are set to true - only Handle can set itself to true
                if (value == false)
                {
                    SharedGroupHandle.Dispose();
                        //this is a safe way to invalidate the handle. Any ongoing transactions will be rolled back
                }
            }
        }

        //a transaction, but using its own code
        //A transaction class does not have a finalizer. Leaked transaction objects will result in open transactions
        //until the user explicitly call close transaction on the shared group, or disposes the shared group
        //note that calling EndTransaction when there is no ongoing transaction will not create any problems. It will be a NoOp
        internal void EndTransaction(bool commit)
        {
            try
            {
                switch (State)
                {
                    case TransactionState.Read:
                        SharedGroupHandle.SharedGroupEndRead();
                        break;
                    case TransactionState.Write:
                        if (commit)
                        {
                            SharedGroupHandle.SharedGroupCommit();
                        }
                        else
                        {
                            SharedGroupHandle.SharedGroupRollback();                        
                        }
                        break;
                }                
            }
            catch (Exception) //something unexpected and bad happened, the shared group and the group should not be used anymore
            {
                IsValid = false;//mark the shared group as invalid
                throw;
            }
        }


        /// <summary>
        /// True if this SharedGroup is currently in a read or a write transaction
        /// </summary>
        /// <returns></returns>
        public bool InTransaction()
        {
            return (State == TransactionState.Read||
                    State == TransactionState.Write);
        }
                 
        private void ValidateNotInTransaction()
        {
            if (InTransaction()) throw new InvalidOperationException("SharedGroup Cannot start a transaction when already inside one");
        }
    }
}