﻿using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Common;


namespace RippleBot.Business
{
    [DataContract]
    internal class NewOrderResponse
    {
        [DataMember] internal OrderData result { get; set; }
        [DataMember] internal string status { get; set; }
        [DataMember] internal string type { get; set; }
    }

    [DataContract]
    internal class OrderData
    {
        private static readonly HashSet<string> _knownFails = new HashSet<string>
        {
            "tefPAST_SEQ", //Message "This sequence number has already past."
        };

        [DataMember] internal string engine_result { get; set; }
        [DataMember] internal int engine_result_code { get; set; }
        [DataMember] internal string engine_result_message { get; set; }
        [DataMember] internal string tx_blob { get; set; }
        [DataMember] internal NOR_TxJson tx_json { get; set; }


        internal ResponseKind ResponseKind
        {
            get
            {
                if (0 == engine_result_code)    //Success
                    return ResponseKind.Success;
                if (_knownFails.Contains(engine_result))
                    return ResponseKind.Error;
                return ResponseKind.FatalError;
            }
        }
    }

    [DataContract]
    internal class NOR_TxJson
    {
        [DataMember] internal string Account { get; set; }
        [DataMember] internal string Fee { get; set; }
        [DataMember] internal long Flags { get; set; }
        [DataMember] internal int Sequence { get; set; }
        [DataMember] internal string SigningPubKey { get; set; }
        [DataMember] internal object TakerGets { get; set; }
        [DataMember] internal object TakerPays { get; set; }
        [DataMember] internal string TransactionType { get; set; }
        [DataMember] internal string TxnSignature { get; set; }
        [DataMember] internal string hash { get; set; }
    }
}
