﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using ICSharpCode.TreeView;

namespace ICSharpCode.SharpDevelop.Dom.ClassBrowser
{
	public interface IClassBrowser
	{
		IAssemblyList MainAssemblyList { get; set; }
		ICollection<IAssemblyList> AssemblyLists { get; }
		
		/*
		  	IAssemblyList MainAssemblyList { get; set; }
			ICollection<IAssemblyList> AssemblyLists { get; }
		 */
	}
}