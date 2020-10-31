﻿using System;
using O10.Client.Web.Portal.Properties;

namespace O10.Client.Web.Portal.Exceptions
{

    [Serializable]
    public class SpAuthenticationProofsFailedException : Exception
    {
        public SpAuthenticationProofsFailedException() : base(Resources.ERR_SP_AUTHENTICATION_FAILED) { }
        public SpAuthenticationProofsFailedException(Exception inner) : base(Resources.ERR_SP_AUTHENTICATION_FAILED, inner) { }
        protected SpAuthenticationProofsFailedException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
